using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
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

/// <summary>Test-only helpers for AutoTest; compiled into Debug builds only (see Spire2PileViewer.csproj).</summary>
public static partial class PileViewer
{
    /// <summary>The count text on each slot of a player's strip, or null without a strip (auto-test).</summary>
    internal static string[]? StripCounts(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && GodotObject.IsInstanceValid(w.Root) && w.Root.Visible ? w.Counts.Select(c => c.Text).ToArray() : null;

    /// <summary>On-screen rects of a player's Pile Viewer and their name block in the player list (auto-test).</summary>
    internal static (Rect2 Strip, Rect2 Row)? StripAndRow(ulong netId)
    {
        if (!_widgets.TryGetValue(netId, out var w) || !GodotObject.IsInstanceValid(w.Root) || PlayerRow(w.Player) is not { } row) return null;
        return (ScreenRect(w.Root), NameBlockRect(row));
    }

    /// <summary>Whether a player's row in the player list is lit (its gold frame) (auto-test).</summary>
    internal static bool RowHighlighted(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && PlayerRow(w.Player) is { } row
        && Traverse.Create(row).Field<bool>("_isHighlighted").Value;

    /// <summary>Whether the row's native hand-count icon is showing (auto-test).</summary>
    internal static bool NativeHandCountShown(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && PlayerRow(w.Player)?.GetNodeOrNull<Control>("%CardCountContainer") is { } c && c.Visible;

    /// <summary>On-screen end of a player's health bar in the player list (auto-test).</summary>
    internal static float HealthBarEnd(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && PlayerRow(w.Player)?.GetNodeOrNull<NHealthBar>("%HealthBar")?.HpBarContainer is Control bar
            ? ScreenRect(bar).End.X : float.NaN;

    /// <summary>The player's row is showing a "relic/card obtained" animation beside it (auto-test).</summary>
    internal static bool RowAnimating(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && PlayerRow(w.Player) is { } row
        && row.GetChildren().Any(c => c is MegaCrit.Sts2.Core.Nodes.Relics.NRelic || c.GetType().Name == "NDeckHistoryEntry");

    /// <summary>On-screen position of a player's character (auto-test).</summary>
    internal static Vector2? CreatureScreenPos(ulong netId) =>
        _widgets.TryGetValue(netId, out var w) && NCombatRoom.Instance?.GetCreatureNode(w.Player.Creature) is { } c
            ? c.GetGlobalTransformWithCanvas().Origin : null;

    internal static Vector2 StripScaleOf(ulong netId) => _widgets.TryGetValue(netId, out var w) ? w.Root.Scale : Vector2.Zero;

    /// <summary>One-line summary of every strip, for logs and the auto-test.</summary>
    internal static string DescribeState()
    {
        if (_widgets.Count == 0) return "no strips";
        return string.Join("; ", _widgets.Values.Select(w =>
            $"player {w.Player.NetId}: visible={w.Root.Visible} pos={w.Root.GlobalPosition} size={w.Root.Size} scale={w.Root.Scale.X:0.##} counts=" +
            string.Join("/", w.Counts.Select(c => c.Text))));
    }

    /// <summary>Feeds a synthetic drag through the real handler (auto-test only).</summary>
    internal static void SimulateDrag(Vector2 delta)
    {
        Widget? widget = _widgets.Values.FirstOrDefault();
        if (widget == null) return;
        Control target = widget.Root;
        ulong id = widget.Player.NetId;
        Vector2 start = target.GlobalPosition + new Vector2(5f, 5f);
        HandleDragInput(id, target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, GlobalPosition = start });
        HandleDragInput(id, target, new InputEventMouseMotion { GlobalPosition = start + delta / 2f });
        HandleDragInput(id, target, new InputEventMouseMotion { GlobalPosition = start + delta });
        HandleDragInput(id, target, new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, GlobalPosition = start + delta });
    }

    /// <summary>On-screen rect of a slot on a player's strip (auto-test only).</summary>
    internal static Rect2? SlotRect(ulong netId, PileType pileType) =>
        _widgets.TryGetValue(netId, out var w) && w.Buttons[Array.IndexOf(StripSlots, pileType)] is Button b && b.IsVisibleInTree()
            ? ScreenRect(b) : null;

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

    /// <summary>Strip index (creation order) of a player's strip, or -1 (auto-test only).</summary>
    internal static int StripIndexOf(ulong netId) => _widgets.Keys.ToList().IndexOf(netId);

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
}
