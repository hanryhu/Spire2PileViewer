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
    }

    /// <summary>Per-frame update while in combat (called by PileViewer).</summary>
    internal static void Update(NCombatRoom room, Func<ulong, Player?> findPlayer)
    {
        if (!Settings.HandViewerEnabled)
        {
            if (ViewedNetId != null || HideOwnHand || _hideButton != null) Reset();
            return;
        }
        if (ViewedNetId is ulong id && findPlayer(id) == null) ViewedNetId = null; // teammate left / not in this combat
        UpdateHideButton(room);
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

    internal static Button? HideButton => _hideButton != null && GodotObject.IsInstanceValid(_hideButton) ? _hideButton : null;

    /// <summary>One-line summary for logs and the auto-test.</summary>
    internal static string DescribeState()
    {
        string button = HideButton is Button b ? $"button '{b.Text}' visible={b.Visible} at {b.GlobalPosition}" : "no button";
        return $"viewing={ViewedNetId?.ToString() ?? "nobody"} hideOwnHand={HideOwnHand} {button}";
    }
}
