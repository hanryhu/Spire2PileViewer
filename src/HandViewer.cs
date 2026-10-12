using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;

namespace Spire2PileViewer;

/// <summary>
/// Hand Viewer (see HAND_VIEWER.md): shows one teammate's hand behind your own. This class owns the view
/// state (whose hand is shown, whether your own hand is hidden) and the "Hide hand" button under End Turn.
/// Driven from PileViewer's per-frame update.
/// </summary>
public static class HandViewer
{
    /// <summary>NetId of the teammate whose hand is shown, or null.</summary>
    public static ulong? ViewedNetId { get; private set; }

    /// <summary>Your own hand is hidden (drawn as an outline) so the teammate's hand is easier to see.</summary>
    public static bool HideOwnHand { get; private set; }

    private static Button? _hideButton;

    private static StyleBoxFlat? _hideButtonStyle;

    /// <summary>Strip "Hand" slot: show this teammate's hand, or hide it if it's already shown. One at a time.</summary>
    public static void Toggle(Player player)
    {
        if (!Settings.HandViewerEnabled) return;
        ViewedNetId = ViewedNetId == player.NetId ? null : player.NetId;
        ModLog.Info($"HandViewer: viewing {(ViewedNetId?.ToString() ?? "nobody")}");
    }

    public static void ToggleHideOwnHand()
    {
        HideOwnHand = !HideOwnHand;
        ModLog.Info($"HandViewer: hide own hand = {HideOwnHand}");
    }

    /// <summary>Combat over / leaving: drop all view state and the button.</summary>
    public static void Reset()
    {
        ViewedNetId = null;
        HideOwnHand = false;
        if (_hideButton != null && GodotObject.IsInstanceValid(_hideButton)) _hideButton.QueueFree();
        _hideButton = null;
        if (_textPanel != null && GodotObject.IsInstanceValid(_textPanel)) _textPanel.QueueFree();
        _textPanel = null;
        _lastText = "";
    }

    /// <summary>Show this teammate's hand (auto-test helper; same as clicking their eye when nothing is shown).</summary>
    internal static void View(ulong netId) => ViewedNetId = netId;

    /// <summary>Per-frame update while in combat (called by PileViewer).</summary>
    internal static void Update(NCombatRoom room, Func<ulong, Player?> findPlayer)
    {
        if (!Settings.HandViewerEnabled)
        {
            if (ViewedNetId != null || HideOwnHand || _hideButton != null) Reset();
            return;
        }
        Player? viewed = ViewedNetId is ulong id ? findPlayer(id) : null;
        if (ViewedNetId != null && viewed == null) ViewedNetId = null; // teammate left / not in this combat
        UpdateHideButton(room);
        UpdateTextPanel(room, viewed);
    }

    private static void UpdateHideButton(NCombatRoom room)
    {
        NEndTurnButton? endTurn = room.Ui?.EndTurnButton;
        bool show = Settings.HideHandButtonEnabled && endTurn != null && endTurn.IsVisibleInTree()
            && NCapstoneContainer.Instance?.InUse != true;
        if (!show)
        {
            if (_hideButton != null && GodotObject.IsInstanceValid(_hideButton)) _hideButton.Visible = false;
            if (!Settings.HideHandButtonEnabled && HideOwnHand) HideOwnHand = false;
            return;
        }
        if (_hideButton == null || !GodotObject.IsInstanceValid(_hideButton)) _hideButton = CreateHideButton(room);

        _hideButton.Visible = true;
        string text = HideOwnHand ? "Show hand" : "Hide hand";
        if (_hideButton.Text != text) _hideButton.Text = text;
        _hideButtonStyle!.BorderColor = HideOwnHand ? new Color(1f, 0.85f, 0.35f) : new Color(0.8f, 0.6f, 0.2f, 0.8f);

        // Centered just below the End Turn button.
        Rect2 rect = endTurn!.GetGlobalRect();
        Vector2 size = _hideButton.Size;
        _hideButton.GlobalPosition = new Vector2(rect.Position.X + (rect.Size.X - size.X) / 2f, rect.End.Y + 4f);
    }

    private static Button CreateHideButton(NCombatRoom room)
    {
        var button = new Button
        {
            Name = "Spire2PileViewer_HideHand",
            Text = "Hide hand",
            FocusMode = Control.FocusModeEnum.None,
            ZIndex = 100,
            CustomMinimumSize = new Vector2(120f, 30f),
            TooltipText = "Hide your own hand (shown as an outline) to see your teammate's hand behind it",
        };
        _hideButtonStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.12f, 0.85f),
            BorderColor = new Color(0.8f, 0.6f, 0.2f, 0.8f),
        };
        _hideButtonStyle.SetBorderWidthAll(2);
        _hideButtonStyle.SetCornerRadiusAll(6);
        _hideButtonStyle.SetContentMarginAll(4);
        var hover = (StyleBoxFlat)_hideButtonStyle.Duplicate();
        hover.BgColor = new Color(0.16f, 0.14f, 0.1f, 0.95f);
        button.AddThemeStyleboxOverride("normal", _hideButtonStyle);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", hover);
        button.AddThemeFontSizeOverride("font_size", 15);
        button.AddThemeColorOverride("font_color", new Color(0.95f, 0.85f, 0.6f));
        button.Pressed += ToggleHideOwnHand;
        room.AddChild(button);
        ModLog.Info("HandViewer: created hide-hand button");
        return button;
    }

    // ---- hv3: text protocol (temporary until hv4 renders real cards) ----

    private static PanelContainer? _textPanel;

    private static Label? _textLabel;

    private static string _lastText = "";

    /// <summary>"The Silent — Hand (7): Strike [1] · Neutralize [0] · ..." with live energy costs.</summary>
    internal static string DescribeHand(Player player)
    {
        if (player.PlayerCombatState == null) return "not in combat";
        var cards = PileType.Hand.GetPile(player).Cards;
        string list = string.Join(" · ", cards.Select(c =>
            $"{c.Title} [{(c.EnergyCost.CostsX ? "X" : c.EnergyCost.GetWithModifiers(CostModifiers.All).ToString())}]"));
        return $"{player.Character.Title.GetFormattedText()} — Hand ({cards.Count}): {(cards.Count == 0 ? "(empty)" : list)}";
    }

    private static void UpdateTextPanel(NCombatRoom room, Player? viewed)
    {
        bool show = viewed != null && NCapstoneContainer.Instance?.InUse != true;
        if (!show)
        {
            if (_textPanel != null && GodotObject.IsInstanceValid(_textPanel)) _textPanel.Visible = false;
            _lastText = "";
            return;
        }
        if (_textPanel == null || !GodotObject.IsInstanceValid(_textPanel))
        {
            _textPanel = new PanelContainer { Name = "Spire2PileViewer_HandText", ZIndex = 100, MouseFilter = Control.MouseFilterEnum.Ignore };
            var style = new StyleBoxFlat { BgColor = new Color(0.05f, 0.05f, 0.08f, 0.85f), BorderColor = new Color(0.8f, 0.6f, 0.2f, 0.8f) };
            style.SetBorderWidthAll(1);
            style.SetCornerRadiusAll(6);
            style.SetContentMarginAll(8);
            _textPanel.AddThemeStyleboxOverride("panel", style);
            _textLabel = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
            _textLabel.AddThemeFontSizeOverride("font_size", 16);
            _textPanel.AddChild(_textLabel);
            room.AddChild(_textPanel);
        }
        _textPanel.Visible = true;
        string text = DescribeHand(viewed!);
        if (text != _lastText)
        {
            _lastText = text;
            _textLabel!.Text = text;
            ModLog.Info("HandViewer: hand text: " + text);
        }
        // Centered above where your own hand sits.
        Vector2 viewport = room.GetViewportRect().Size;
        _textPanel.GlobalPosition = new Vector2((viewport.X - _textPanel.Size.X) / 2f, viewport.Y * 0.62f);
    }

    internal static string CurrentText => _lastText;

    internal static Button? HideButton => _hideButton != null && GodotObject.IsInstanceValid(_hideButton) ? _hideButton : null;

    /// <summary>One-line summary for logs and the auto-test.</summary>
    internal static string DescribeState()
    {
        string button = HideButton is Button b ? $"button '{b.Text}' visible={b.Visible} at {b.GlobalPosition}" : "no button";
        return $"viewing={ViewedNetId?.ToString() ?? "nobody"} hideOwnHand={HideOwnHand} {button}";
    }
}
