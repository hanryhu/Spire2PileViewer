using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// <summary>The three piles that open a pile view (also the hotkey targets).</summary>
    internal static readonly PileType[] SlotPiles = { PileType.Draw, PileType.Exhaust, PileType.Discard };

    /// <summary>Strip slots left to right. Hand toggles the Hand Viewer instead of opening a pile.</summary>
    internal static readonly PileType[] StripSlots = { PileType.Draw, PileType.Hand, PileType.Exhaust, PileType.Discard };

    private const string PeekTexturePath = "res://images/atlases/ui_atlas.sprites/peek_button.tres";

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
        public readonly Label[] Counts = new Label[StripSlots.Length];
        public readonly Control?[] Icons = new Control?[StripSlots.Length];
        public readonly Control?[] Badges = new Control?[StripSlots.Length];
        public readonly Button?[] Buttons = new Button?[StripSlots.Length];

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

    private static readonly HashSet<PileType> _loggedNativeTrees = new();

    private static readonly HashSet<PileType> _loggedBadges = new();

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
        HandViewer.Reset();
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

        HandViewer.Update(room, id => state.Players.FirstOrDefault(p => p != null && p.NetId == id && p.NetId != localNetId));

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

        for (int i = 0; i < StripSlots.Length; i++)
        {
            if (StripSlots[i] == PileType.Hand && widget.Buttons[i] is Button handButton)
            {
                handButton.Visible = Settings.HandViewerEnabled;
                // Highlight the Hand slot of the teammate whose hand is being shown.
                bool viewing = HandViewer.ViewedNetId == widget.Player.NetId;
                if (widget.Icons[i] is Control eye) eye.Modulate = viewing ? new Color(1.3f, 1.15f, 0.55f) : Colors.White;
            }
            string text = StripSlots[i].GetPile(widget.Player).Cards.Count.ToString();
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
        widget.Root.GlobalPosition = creatureNode.GlobalPosition + AnchorOffset + Settings.GetOffset(widget.Player.NetId)
            - new Vector2(size.X / 2f, size.Y);

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
        root.GuiInput += ev => HandleDragInput(player.NetId, root, ev);
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
        for (int i = 0; i < StripSlots.Length; i++)
        {
            widget.Buttons[i] = CreateSlot(room, widget, StripSlots[i], out widget.Counts[i]);
            row.AddChild(widget.Buttons[i]);
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
            TooltipText = (pileType == PileType.Hand ? "Show this teammate's hand behind yours" : PileName(pileType)) + "\n" + DragHint,
        };
        button.GuiInput += ev => HandleDragInput(widget.Player.NetId, button, ev);

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
            widget.Icons[Array.IndexOf(StripSlots, pileType)] = icon;
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

        // Count badge copied from the native pile button (each pile has its own placement and art — e.g. the
        // exhaust badge is purple and centered on the icon), scaled from the native button to the slot.
        BadgeStyle badgeStyle = GetBadgeStyle(room, pileType);
        var badge = new TextureRect
        {
            Texture = badgeStyle.Texture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Size = badgeStyle.Size,
            Position = badgeStyle.Position,
            SelfModulate = badgeStyle.Tint,
        };
        button.AddChild(badge);
        badge.PivotOffset = badgeStyle.Size / 2f;
        int slot = Array.IndexOf(StripSlots, pileType);
        widget.Badges[slot] = badge;

        count = new Label
        {
            Text = "0",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        count.AddThemeFontSizeOverride("font_size", badgeStyle.FontSize);
        count.AddThemeColorOverride("font_color", badgeStyle.TextColor);
        count.AddThemeConstantOverride("outline_size", badgeStyle.OutlineSize);
        count.AddThemeColorOverride("font_outline_color", badgeStyle.OutlineColor);
        badge.AddChild(count);
        count.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        if (pileType == PileType.Hand) button.Pressed += () => HandViewer.Toggle(widget.Player);
        else button.Pressed += () => OnSlotPressed(widget, pileType);
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

    private const string DragHint = "Shift+drag to move this strip (reset in the F1 menu)";

    private static bool _dragging;

    private static ulong _dragNetId;

    private static Vector2 _dragStartMouse;

    private static Vector2 _dragStartOffset;

    /// <summary>
    /// Shift+left-drag moves just that teammate's strip (its own offset, saved on release; reset from the
    /// F1 menu). Shift events are consumed so they don't also press a slot.
    /// </summary>
    internal static void HandleDragInput(ulong netId, Control control, InputEvent ev)
    {
        if (ev is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left && mb.Pressed && mb.ShiftPressed)
            {
                _dragging = true;
                _dragNetId = netId;
                _dragStartMouse = mb.GlobalPosition;
                _dragStartOffset = Settings.GetOffset(netId);
                control.AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Left && !mb.Pressed && _dragging)
            {
                _dragging = false;
                Settings.Save();
                ModLog.Info($"PileViewer: strip offset saved for player {_dragNetId}: {Settings.GetOffset(_dragNetId)}");
                control.AcceptEvent();
            }
        }
        else if (ev is InputEventMouseMotion mm && _dragging)
        {
            Settings.SetOffset(_dragNetId, _dragStartOffset + (mm.GlobalPosition - _dragStartMouse));
            control.AcceptEvent();
        }
    }

    /// <summary>Feeds a synthetic Shift+drag through the real handler (auto-test only).</summary>
    internal static void SimulateDrag(Vector2 delta)
    {
        Widget? widget = _widgets.Values.FirstOrDefault();
        if (widget == null) return;
        Control target = widget.Root;
        ulong id = widget.Player.NetId;
        Vector2 start = target.GlobalPosition + new Vector2(5f, 5f);
        HandleDragInput(id, target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, ShiftPressed = true, GlobalPosition = start });
        HandleDragInput(id, target, new InputEventMouseMotion { GlobalPosition = start + delta / 2f, ShiftPressed = true });
        HandleDragInput(id, target, new InputEventMouseMotion { GlobalPosition = start + delta, ShiftPressed = true });
        HandleDragInput(id, target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = start + delta });
    }

    /// <summary>Presses a slot on the first strip, as if clicked (auto-test only).</summary>
    internal static void SimulatePress(PileType pileType) => SimulatePress(0, pileType);

    /// <summary>Presses a slot on strip #index (in creation order) through the slot button's real Pressed signal.</summary>
    internal static void SimulatePress(int index, PileType pileType)
    {
        Widget? widget = _widgets.Values.Skip(index).FirstOrDefault();
        int slot = Array.IndexOf(StripSlots, pileType);
        if (widget == null || slot < 0 || widget.Buttons[slot] is not Button button)
        {
            ModLog.Info($"PileViewer: SimulatePress({index}, {pileType}) with no such strip/slot");
            return;
        }
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    /// <summary>Whether each strip's Hand slot is shown (auto-test only).</summary>
    internal static string DescribeHandSlots() => string.Join(", ", _widgets.Values.Select(w =>
        $"{w.Player.NetId}:{(w.Buttons[Array.IndexOf(StripSlots, PileType.Hand)]?.Visible == true ? "shown" : "hidden")}"));

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
    private static void OnSlotPressed(Widget widget, PileType pileType) => OpenPile(widget.Player, pileType, "clicked");

    /// <summary>
    /// The other players in party order (the order everyone's client agrees on), with their P-number.
    /// Empty outside a run.
    /// </summary>
    internal static List<(int number, Player player)> Teammates()
    {
        var result = new List<(int, Player)>();
        var players = CombatManager.Instance.DebugOnlyGetState()?.Players ?? RunManager.Instance.DebugOnlyGetState()?.Players;
        if (players == null) return result;
        ulong localNetId = RunManager.Instance?.NetService?.NetId ?? 0;
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i] != null && players[i].NetId != localNetId) result.Add((i + 1, players[i]));
        }
        return result;
    }

    /// <summary>Hotkey entry point: opens teammate #index's pile (0-based among <see cref="Teammates"/>).</summary>
    internal static bool OpenTeammatePile(int teammateIndex, PileType pileType)
    {
        if (!CombatManager.Instance.IsInProgress) return false;
        var teammates = Teammates();
        if (teammateIndex >= teammates.Count) return false;
        OpenPile(teammates[teammateIndex].player, pileType, "hotkey");
        return true;
    }

    private static void OpenPile(Player player, PileType pileType, string source)
    {
        _widgets.TryGetValue(player.NetId, out Widget? widget);
        string text = DescribePile(player, pileType);
        ModLog.Info($"PileViewer: {source} {pileType} for player {player.NetId}: " + text.Replace('\n', '|'));
        if (player.PlayerCombatState == null || !CombatManager.Instance.IsInProgress) return;
        if (Settings.TextList && widget != null)
        {
            ToggleTextPopup(widget, pileType, text);
            return;
        }

        try
        {
            widget?.ClosePopup();
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
            if (widget != null) ToggleTextPopup(widget, pileType, text);
        }
    }

    /// <summary>Prefixes the native screen's bottom caption with whose pile this is.</summary>
    private static readonly HashSet<PileType> _loggedCaptions = new();

    /// <summary>
    /// The native captions are written for your own piles ("...shuffled into your draw pile...", "...at the
    /// start of each turn"); point them at the teammate instead. English wording only — other languages keep
    /// the game's text (the gold name line above still says whose pile it is).
    /// </summary>
    internal static string PersonalizeCaption(string text, string owner)
    {
        text = Regex.Replace(text, @"\b[Yy]our\b", owner + "'s");
        text = Regex.Replace(text, @"\beach turn\b", owner + "'s turn");
        text = Regex.Replace(text, @"\bwere exhausted this combat\b", "were exhausted by " + owner + " this combat");
        text = Regex.Replace(text, @"\b[Yy]ou\b", owner);
        return text;
    }

    private static void LabelScreen(NCardPileScreen screen, Player player)
    {
        try
        {
            if (Traverse.Create(screen).Field("_bottomLabel").GetValue() is RichTextLabel label)
            {
                string owner = player.Character.Title.GetFormattedText();
                string original = label.Text;
                label.Text = $"[center][gold]{owner}[/gold][/center]\n" + PersonalizeCaption(original, owner);
                if (_loggedCaptions.Add(screen.Pile.Type))
                {
                    ModLog.Info($"PileViewer: {screen.Pile.Type} caption '{original}' → '{label.Text}'");
                }
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
    private static NCombatCardPile? NativeButton(NCombatRoom room, PileType pileType)
    {
        NCombatCardPile? native = pileType switch
        {
            PileType.Draw => room.Ui?.DrawPile,
            PileType.Discard => room.Ui?.DiscardPile,
            PileType.Exhaust => room.Ui?.ExhaustPile,
            _ => null,
        };
        if (native != null && _loggedNativeTrees.Add(pileType))
        {
            ModLog.Info($"PileViewer: native {pileType} pile button tree:\n" + DescribeTree(native, 0));
        }
        return native;
    }

    private readonly record struct BadgeStyle(Texture2D? Texture, Vector2 Position, Vector2 Size, Color Tint,
        Color TextColor, Color OutlineColor, int OutlineSize, int FontSize);

    /// <summary>
    /// Placement and look of the native button's count (CountContainer, its optional Background badge, and the
    /// Count label's colors/outline/size), mapped from the native button onto a slot. Draw/discard use a red
    /// badge in a corner; exhaust has no badge and centers a purple-outlined number on the icon.
    /// Falls back to a bottom-right badge.
    /// </summary>
    private static BadgeStyle GetBadgeStyle(NCombatRoom room, PileType pileType)
    {
        const float fallbackSize = 26f;
        var fallback = new BadgeStyle(LoadTexture(BadgeTexturePath),
            new Vector2(SlotSize - fallbackSize + 2f, SlotSize - fallbackSize + 2f),
            new Vector2(fallbackSize, fallbackSize), Colors.White, Colors.White, Colors.Black, 4, 15);
        try
        {
            // The Hand slot has no native pile button; it borrows the draw pile's badge.
            NCombatCardPile? native = NativeButton(room, pileType == PileType.Hand ? PileType.Draw : pileType);
            Control? container = native?.GetNodeOrNull<Control>("CountContainer");
            TextureRect? background = container?.GetNodeOrNull<TextureRect>("Background");
            Label? label = container?.GetNodeOrNull<Label>("Count");
            Control? icon = native?.GetNodeOrNull<Control>("Icon");
            if (container == null || label == null || icon == null || icon.Size.X <= 0f) return fallback;

            // Map native coordinates (relative to the icon, which fills our slot) onto the slot.
            float k = SlotSize / icon.Size.X;
            Control frame = (Control?)background ?? container;
            Vector2 framePos = background != null ? container.Position + background.Position * container.Scale : container.Position;
            Vector2 size = frame.Size * container.Scale * (background?.Scale ?? Vector2.One) * k;
            Vector2 position = (framePos - icon.Position) * k;
            Color tint = container.Modulate * container.SelfModulate * (background != null ? background.Modulate * background.SelfModulate : Colors.White);
            var style = new BadgeStyle(background?.Texture, position, size, tint,
                label.GetThemeColor("font_color"),
                label.GetThemeColor("font_outline_color"),
                Math.Max(1, (int)Math.Round(label.GetThemeConstant("outline_size") * k)),
                Math.Max(9, (int)Math.Round(label.GetThemeFontSize("font_size") * k)));
            if (_loggedBadges.Add(pileType)) ModLog.Info($"PileViewer: {pileType} badge {style} (k={k})");
            return style;
        }
        catch (Exception ex)
        {
            ModLog.Error("PileViewer.GetBadgeStyle", ex);
            return fallback;
        }
    }

    private static Texture2D? GetPileTexture(NCombatRoom room, PileType pileType)
    {
        // The Hand slot uses the eye icon of the native "peek" button (shown when a card picker hides your hand).
        if (pileType == PileType.Hand) return LoadTexture(PeekTexturePath);
        try
        {
            NCombatCardPile? native = NativeButton(room, pileType);
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

    internal static string DescribeTree(Node node, int depth)
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

    internal static string PileName(PileType pileType) => pileType switch
    {
        PileType.Draw => "Draw pile",
        PileType.Hand => "Hand",
        PileType.Exhaust => "Exhaust pile",
        PileType.Discard => "Discard pile",
        _ => pileType.ToString(),
    };
}
