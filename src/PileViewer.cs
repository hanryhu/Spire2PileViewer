using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>
/// A 3-slot strip (draw | exhaust | discard) hovering above each teammate in combat.
/// Driven by SceneTree.ProcessFrame because mod-defined Godot subclasses are not registered
/// scripts, so engine virtuals like _Process are never called on them.
/// </summary>
public static class PileViewer
{
    internal static readonly PileType[] SlotPiles = { PileType.Draw, PileType.Exhaust, PileType.Discard };

    private const float SlotSize = 56f;

    private static readonly Color StripBg = new(0.05f, 0.05f, 0.08f);

    private static readonly Color StripBorder = new(0.8f, 0.6f, 0.2f);

    // Relative to the creature node's origin. BetterSpire2's incoming-damage label sits at (10, -320);
    // the strip's bottom-center goes above that.
    private static readonly Vector2 AnchorOffset = new(0f, -350f);

    // Launch with -pileviewerself to also show a strip for the local player (handy for solo testing).
    // The solo auto-test also implies it, since a solo run has no teammates.
    private static readonly bool IncludeSelf = OS.GetCmdlineArgs().Contains("-pileviewerself");

    private sealed class Widget
    {
        public required Player Player;
        public required PanelContainer Root;
        public required StyleBoxFlat Style;
        public readonly Label[] Counts = new Label[SlotPiles.Length];
        public readonly Control?[] Icons = new Control?[SlotPiles.Length];
        public readonly Control?[] Badges = new Control?[SlotPiles.Length];

        // Text popup listing one pile's cards (milestone 2); null when closed.
        public PanelContainer? Popup;
        public Label? PopupLabel;
        public PileType PopupPile;

        public void ClosePopup()
        {
            if (Popup != null && GodotObject.IsInstanceValid(Popup)) Popup.QueueFree();
            Popup = null;
            PopupLabel = null;
        }

        public void Free()
        {
            ClosePopup();
            if (GodotObject.IsInstanceValid(Root)) Root.QueueFree();
        }
    }

    private static readonly Dictionary<ulong, Widget> _widgets = new();

    private static bool _frameHooked;

    private static bool _loggedIconTree;

    private static bool _loggedFrameError;

    public static void OnCombatSetUp()
    {
        if (_frameHooked) return;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += OnFrame;
            _frameHooked = true;
            ModLog.Info($"PileViewer: hooked ProcessFrame (includeSelf={IncludeSelf})");
        }
    }

    public static void Hide()
    {
        foreach (var widget in _widgets.Values) widget.Free();
        _widgets.Clear();
    }

    private static void OnFrame()
    {
        try
        {
            Update();
        }
        catch (Exception ex)
        {
            if (_loggedFrameError) return;
            _loggedFrameError = true;
            ModLog.Error("PileViewer.Update", ex);
        }
    }

    private static void Update()
    {
        NCombatRoom? room = NCombatRoom.Instance;
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (room == null || state == null || !CombatManager.Instance.IsInProgress || !Settings.Enabled)
        {
            if (_widgets.Count > 0) Hide();
            return;
        }

        ulong localNetId = RunManager.Instance?.NetService?.NetId ?? 0;
        bool includeSelf = Settings.ShowSelf || IncludeSelf || (AutoTest.Enabled && state.Players.Count == 1);
        var players = state.Players.Where(p => p != null && (includeSelf || p.NetId != localNetId)).ToList();

        foreach (var stale in _widgets.Keys.Where(id => players.All(p => p.NetId != id)).ToList())
        {
            _widgets[stale].Free();
            _widgets.Remove(stale);
        }

        foreach (var player in players)
        {
            if (!_widgets.TryGetValue(player.NetId, out var widget) || !GodotObject.IsInstanceValid(widget.Root))
            {
                widget = CreateWidget(room, player);
                _widgets[player.NetId] = widget;
            }
            UpdateWidget(room, widget);
        }
    }

    private static void UpdateWidget(NCombatRoom room, Widget widget)
    {
        NCreature? creatureNode = room.GetCreatureNode(widget.Player.Creature);
        // Hide under full-screen overlays (pile screens, deck view, map, ...) — the strip would draw on top of them.
        bool overlayOpen = NCapstoneContainer.Instance?.InUse == true;
        if (creatureNode == null || widget.Player.PlayerCombatState == null || overlayOpen)
        {
            widget.Root.Visible = false;
            widget.ClosePopup();
            return;
        }
        widget.Root.Visible = true;

        for (int i = 0; i < SlotPiles.Length; i++)
        {
            string text = SlotPiles[i].GetPile(widget.Player).Cards.Count.ToString();
            if (widget.Counts[i].Text == text) continue;
            // Native pile buttons bump the icon and count when a card arrives; do the same.
            bool grew = int.TryParse(widget.Counts[i].Text, out int old) && int.TryParse(text, out int now) && now > old;
            widget.Counts[i].Text = text;
            if (grew)
            {
                Bump(widget.Icons[i]);
                Bump(widget.Badges[i]);
            }
        }

        // Settings from the F1 menu: size and background opacity.
        if (widget.Root.Scale.X != Settings.Scale) widget.Root.Scale = new Vector2(Settings.Scale, Settings.Scale);
        if (widget.Style.BgColor.A != Settings.Opacity)
        {
            widget.Style.BgColor = new Color(StripBg, Settings.Opacity);
            widget.Style.BorderColor = new Color(StripBorder, Settings.Opacity);
        }

        Vector2 size = widget.Root.Size * Settings.Scale;
        widget.Root.GlobalPosition = creatureNode.GlobalPosition + AnchorOffset + Settings.Offset - new Vector2(size.X / 2f, size.Y);

        if (widget.Popup != null && widget.PopupLabel != null && GodotObject.IsInstanceValid(widget.Popup))
        {
            string text = DescribePile(widget.Player, widget.PopupPile);
            if (widget.PopupLabel.Text != text) widget.PopupLabel.Text = text;
            Vector2 popupSize = widget.Popup.Size;
            widget.Popup.GlobalPosition = widget.Root.GlobalPosition + new Vector2((size.X - popupSize.X) / 2f, -popupSize.Y - 6f);
        }
    }

    /// <summary>
    /// "Ironclad — Draw pile (5)" followed by one line per distinct card ("3× Strike"), sorted by name
    /// so the draw order is not revealed (the native draw-pile screen also sorts).
    /// </summary>
    internal static string DescribePile(Player player, PileType pileType)
    {
        if (player.PlayerCombatState == null) return PileName(pileType) + ": not in combat";
        IReadOnlyList<CardModel> cards = pileType.GetPile(player).Cards;
        string header = $"{player.Character.Title.GetFormattedText()} — {PileName(pileType)} ({cards.Count})";
        if (cards.Count == 0) return header + "\n(empty)";
        var lines = cards.GroupBy(c => c.Title)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Count()}× {g.Key}" : g.Key);
        return header + "\n" + string.Join("\n", lines);
    }

    private static Widget CreateWidget(NCombatRoom room, Player player)
    {
        var root = new PanelContainer
        {
            Name = $"Spire2PileViewer_{player.NetId}",
            ZIndex = 100,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = DragHint,
        };
        root.GuiInput += ev => HandleDragInput(root, ev);
        var style = new StyleBoxFlat
        {
            BgColor = new Color(StripBg, Settings.Opacity),
            BorderColor = new Color(StripBorder, Settings.Opacity),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(4);
        root.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        root.AddChild(row);

        var widget = new Widget { Player = player, Root = root, Style = style };
        for (int i = 0; i < SlotPiles.Length; i++)
        {
            row.AddChild(CreateSlot(room, widget, SlotPiles[i], out widget.Counts[i]));
        }

        room.AddChild(root);
        ModLog.Info($"PileViewer: created strip for player {player.NetId}");
        return widget;
    }

    private static Button CreateSlot(NCombatRoom room, Widget widget, PileType pileType, out Label count)
    {
        var button = new Button
        {
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(SlotSize, SlotSize),
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = PileName(pileType) + "\n" + DragHint,
        };
        button.GuiInput += ev => HandleDragInput(button, ev);

        Texture2D? texture = GetPileTexture(room, pileType);
        if (texture != null)
        {
            var icon = new TextureRect
            {
                Texture = texture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = Control.MouseFilterEnum.Ignore,
            };
            button.AddChild(icon);
            widget.Icons[Array.IndexOf(SlotPiles, pileType)] = icon;
            icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            // Hover bump like the native pile buttons (1.25x, eased back on exit).
            icon.PivotOffset = new Vector2(SlotSize, SlotSize) / 2f;
            button.MouseEntered += () => TweenScale(icon, 1.25f, 0.05);
            button.MouseExited += () => TweenScale(icon, 1f, 0.3);
        }
        else
        {
            button.Text = PileName(pileType)[..2];
        }

        // Count badge, styled like the native pile button's (badge texture + centered number, bottom-right).
        const float badgeSize = 26f;
        var badge = new TextureRect
        {
            Texture = LoadTexture(BadgeTexturePath),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Size = new Vector2(badgeSize, badgeSize),
            Position = new Vector2(SlotSize - badgeSize + 2f, SlotSize - badgeSize + 2f),
        };
        button.AddChild(badge);
        badge.PivotOffset = new Vector2(badgeSize, badgeSize) / 2f;
        int slot = Array.IndexOf(SlotPiles, pileType);
        widget.Badges[slot] = badge;

        count = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        count.AddThemeFontSizeOverride("font_size", 15);
        count.AddThemeColorOverride("font_color", Colors.White);
        count.AddThemeConstantOverride("outline_size", 4);
        count.AddThemeColorOverride("font_outline_color", Colors.Black);
        badge.AddChild(count);
        count.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        button.Pressed += () => OnSlotPressed(widget, pileType);
        return button;
    }

    /// <summary>One-line summary of every strip, for logs and the auto-test.</summary>
    internal static string DescribeState()
    {
        if (_widgets.Count == 0) return "no strips";
        return string.Join("; ", _widgets.Values.Select(w =>
            $"player {w.Player.NetId}: visible={w.Root.Visible} pos={w.Root.GlobalPosition} size={w.Root.Size} scale={w.Root.Scale.X:0.##} counts=" +
            string.Join("/", w.Counts.Select(c => c.Text))));
    }

    /// <summary>Presses a slot on the first strip, as if clicked (auto-test only).</summary>
    private const string DragHint = "Shift+drag to move the strips (reset in the F1 menu)";

    private static bool _dragging;

    private static Vector2 _dragStartMouse;

    private static Vector2 _dragStartOffset;

    /// <summary>
    /// Shift+left-drag on any strip moves all strips (one shared offset, saved on release; reset from the
    /// F1 menu). Shift events are consumed so they don't also press a slot.
    /// </summary>
    internal static void HandleDragInput(Control control, InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed && mb.ShiftPressed)
            {
                _dragging = true;
                _dragStartMouse = mb.GlobalPosition;
                _dragStartOffset = Settings.Offset;
                control.AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed && _dragging)
            {
                _dragging = false;
                Settings.Save();
                ModLog.Info($"PileViewer: strip offset saved {Settings.Offset}");
                control.AcceptEvent();
            }
        }
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            Settings.Offset = _dragStartOffset + (mm.GlobalPosition - _dragStartMouse);
            control.AcceptEvent();
        }
    }

    /// <summary>Feeds a synthetic Shift+drag through the real handler (auto-test only).</summary>
    internal static void SimulateDrag(Vector2 delta)
    {
        Widget? widget = _widgets.Values.FirstOrDefault();
        if (widget == null) return;
        Control target = widget.Root;
        Vector2 start = target.GlobalPosition + new Vector2(5f, 5f);
        HandleDragInput(target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true, GlobalPosition = start });
        HandleDragInput(target, new InputEventMouseMotion { GlobalPosition = start + delta / 2f, ShiftPressed = true });
        HandleDragInput(target, new InputEventMouseMotion { GlobalPosition = start + delta, ShiftPressed = true });
        HandleDragInput(target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = start + delta });
    }

    internal static void SimulatePress(PileType pileType)
    {
        Widget? widget = _widgets.Values.FirstOrDefault();
        if (widget == null)
        {
            ModLog.Info("PileViewer: SimulatePress with no strips");
            return;
        }
        OnSlotPressed(widget, pileType);
    }

    /// <summary>What the last press opened, for the auto-test log.</summary>
    internal static string DescribeOpenView()
    {
        if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is NCardPileScreen screen)
        {
            Player? owner = _widgets.Values.Select(w => w.Player)
                .FirstOrDefault(p => p.PlayerCombatState != null && SlotPiles.Any(t => t.GetPile(p) == screen.Pile));
            return $"native screen: {screen.Pile.Type} pile of player {owner?.NetId.ToString() ?? "?"} ({screen.Pile.Cards.Count} cards)";
        }
        Widget? withPopup = _widgets.Values.FirstOrDefault(w => w.Popup != null && GodotObject.IsInstanceValid(w.Popup));
        if (withPopup != null) return $"text popup: {withPopup.PopupLabel?.Text.Replace('\n', '|')}";
        return "nothing open";
    }

    /// <summary>
    /// Opens the game's own card-pile screen on the teammate's pile, mirroring NCombatCardPile.OnRelease:
    /// cancel targeting, toggle closed if that pile is already open, thought bubble if the pile is empty.
    /// Falls back to the milestone-2 text popup if the native screen can't be opened.
    /// </summary>
    private static void OnSlotPressed(Widget widget, PileType pileType)
    {
        Player player = widget.Player;
        string text = DescribePile(player, pileType);
        ModLog.Info($"PileViewer: clicked {pileType} for player {player.NetId}: " + text.Replace('\n', '|'));
        if (player.PlayerCombatState == null || !CombatManager.Instance.IsInProgress) return;
        if (Settings.TextList)
        {
            ToggleTextPopup(widget, pileType, text);
            return;
        }

        try
        {
            widget.ClosePopup();
            CardPile pile = pileType.GetPile(player);
            if (NTargetManager.Instance.IsInSelection) NTargetManager.Instance.CancelTargeting();

            NCapstoneContainer? capstone = NCapstoneContainer.Instance;
            if (pile.IsEmpty)
            {
                if (capstone?.InUse == true) capstone.Close();
                ShowEmptyBubble(player, pileType);
                return;
            }
            if (capstone?.CurrentCapstoneScreen is NCardPileScreen open && open.Pile == pile)
            {
                capstone.Close();
                return;
            }

            NCardPileScreen screen = NCardPileScreen.ShowScreen(pile, Array.Empty<string>());
            LabelScreen(screen, player);
        }
        catch (Exception ex)
        {
            ModLog.Error("PileViewer: native pile screen failed, using text popup", ex);
            ToggleTextPopup(widget, pileType, text);
        }
    }

    /// <summary>Prefixes the native screen's bottom caption with whose pile this is.</summary>
    private static void LabelScreen(NCardPileScreen screen, Player player)
    {
        try
        {
            if (Traverse.Create(screen).Field("_bottomLabel").GetValue() is RichTextLabel label)
            {
                string owner = player.Character.Title.GetFormattedText();
                label.Text = $"[center][gold]{owner}[/gold][/center]\n" + label.Text;
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("PileViewer.LabelScreen", ex);
        }
    }

    private static void ShowEmptyBubble(Player player, PileType pileType)
    {
        string key = pileType == PileType.Draw ? "OPEN_EMPTY_DRAW" : "OPEN_EMPTY_DISCARD";
        string message = pileType == PileType.Exhaust && !LocString.Exists("combat_messages", "OPEN_EMPTY_EXHAUST")
            ? "No exhausted cards."
            : new LocString("combat_messages", pileType == PileType.Exhaust ? "OPEN_EMPTY_EXHAUST" : key).GetFormattedText();
        var bubble = NThoughtBubbleVfx.Create(message, player.Creature, 2.0);
        NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(bubble);
    }

    private static void ToggleTextPopup(Widget widget, PileType pileType, string text)
    {
        bool sameOpen = widget.Popup != null && GodotObject.IsInstanceValid(widget.Popup) && widget.PopupPile == pileType;
        widget.ClosePopup();
        if (sameOpen) return;

        var popup = new PanelContainer { ZIndex = 101, MouseFilter = Control.MouseFilterEnum.Ignore };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.12f, 0.92f),
            BorderColor = new Color(0.8f, 0.6f, 0.2f, 0.9f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(8);
        popup.AddThemeStyleboxOverride("panel", style);

        var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", new Color(0.92f, 0.92f, 0.92f));
        popup.AddChild(label);

        widget.Root.GetParent().AddChild(popup);
        widget.Popup = popup;
        widget.PopupLabel = label;
        widget.PopupPile = pileType;
    }

    /// <summary>Native AddCard animation: snap to 1.25x, ease back to 1x over 0.5s.</summary>
    private static void Bump(Control? control)
    {
        if (control == null || !GodotObject.IsInstanceValid(control)) return;
        control.Scale = new Vector2(1.25f, 1.25f);
        TweenScale(control, 1f, 0.5);
    }

    private static void TweenScale(Control control, float scale, double seconds)
    {
        if (!GodotObject.IsInstanceValid(control)) return;
        control.CreateTween().TweenProperty(control, "scale", new Vector2(scale, scale), seconds)
            .SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
    }

    private const string BadgeTexturePath = "res://images/packed/combat_ui/pile_button_count.png";

    /// <summary>
    /// The icon texture of the local player's native pile button, so the strip matches the game's art.
    /// (Duplicating the native Icon node rendered blank, so we build a fresh TextureRect instead.)
    /// </summary>
    private static Texture2D? GetPileTexture(NCombatRoom room, PileType pileType)
    {
        try
        {
            NCombatCardPile? native = pileType switch
            {
                PileType.Draw => room.Ui?.DrawPile,
                PileType.Discard => room.Ui?.DiscardPile,
                PileType.Exhaust => room.Ui?.ExhaustPile,
                _ => null,
            };
            if (native != null && !_loggedIconTree)
            {
                _loggedIconTree = true;
                ModLog.Info("PileViewer: native pile button tree:\n" + DescribeTree(native, 0));
            }
            if (native?.GetNodeOrNull<TextureRect>("Icon")?.Texture is Texture2D texture) return texture;
        }
        catch (Exception ex)
        {
            ModLog.Error("PileViewer.GetPileTexture", ex);
        }
        return LoadTexture($"res://images/packed/combat_ui/{pileType.ToString().ToLowerInvariant()}_pile.png");
    }

    private static Texture2D? LoadTexture(string path)
    {
        try
        {
            return ResourceLoader.Exists(path) ? ResourceLoader.Load<Texture2D>(path) : null;
        }
        catch (Exception ex)
        {
            ModLog.Error("PileViewer.LoadTexture " + path, ex);
            return null;
        }
    }

    private static string DescribeTree(Node node, int depth)
    {
        string line = new string(' ', depth * 2) + $"{node.Name} ({node.GetClass()})";
        if (node is Control c) line += $" pos={c.Position} size={c.Size} scale={c.Scale}";
        if (node is TextureRect tr && tr.Texture != null) line += $" tex={tr.Texture.ResourcePath}";
        if (node is Sprite2D sp && sp.Texture != null) line += $" tex={sp.Texture.ResourcePath}";
        var lines = new List<string> { line };
        if (depth < 4)
        {
            foreach (Node child in node.GetChildren()) lines.Add(DescribeTree(child, depth + 1));
        }
        return string.Join("\n", lines);
    }

    private static string PileName(PileType pileType) => pileType switch
    {
        PileType.Draw => "Draw pile",
        PileType.Exhaust => "Exhaust pile",
        PileType.Discard => "Discard pile",
        _ => pileType.ToString(),
    };
}
