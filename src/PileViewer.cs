using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
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

    // Relative to the creature node's origin. BetterSpire2's incoming-damage label sits at (10, -320);
    // the strip's bottom-center goes above that.
    private static readonly Vector2 AnchorOffset = new(0f, -350f);

    // Launch with -pileviewerself to also show a strip for the local player (handy for solo testing).
    private static readonly bool IncludeSelf = AutoTest.Enabled || OS.GetCmdlineArgs().Contains("-pileviewerself");

    private sealed class Widget
    {
        public required Player Player;
        public required PanelContainer Root;
        public readonly Label[] Counts = new Label[SlotPiles.Length];

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
        if (room == null || state == null || !CombatManager.Instance.IsInProgress)
        {
            if (_widgets.Count > 0) Hide();
            return;
        }

        ulong localNetId = RunManager.Instance?.NetService?.NetId ?? 0;
        var players = state.Players.Where(p => p != null && (IncludeSelf || p.NetId != localNetId)).ToList();

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
        if (creatureNode == null || widget.Player.PlayerCombatState == null)
        {
            widget.Root.Visible = false;
            widget.ClosePopup();
            return;
        }
        widget.Root.Visible = true;

        for (int i = 0; i < SlotPiles.Length; i++)
        {
            string text = SlotPiles[i].GetPile(widget.Player).Cards.Count.ToString();
            if (widget.Counts[i].Text != text) widget.Counts[i].Text = text;
        }

        Vector2 size = widget.Root.Size;
        widget.Root.GlobalPosition = creatureNode.GlobalPosition + AnchorOffset - new Vector2(size.X / 2f, size.Y);

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
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.05f, 0.05f, 0.08f, 0.75f),
            BorderColor = new Color(0.8f, 0.6f, 0.2f, 0.8f),
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(4);
        root.AddThemeStyleboxOverride("panel", style);

        var row = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 4);
        root.AddChild(row);

        var widget = new Widget { Player = player, Root = root };
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
            TooltipText = PileName(pileType),
        };

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
            icon.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
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
            $"player {w.Player.NetId}: visible={w.Root.Visible} pos={w.Root.GlobalPosition} size={w.Root.Size} counts=" +
            string.Join("/", w.Counts.Select(c => c.Text))));
    }

    /// <summary>Presses a slot on the first strip, as if clicked (auto-test only).</summary>
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

    private static void OnSlotPressed(Widget widget, PileType pileType)
    {
        string text = DescribePile(widget.Player, pileType);
        ModLog.Info($"PileViewer: clicked {pileType} for player {widget.Player.NetId}: " + text.Replace('\n', '|'));

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
