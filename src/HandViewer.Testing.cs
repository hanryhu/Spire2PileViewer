using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>Test-only helpers for AutoTest; compiled into Debug builds only (see Spire2PileViewer.csproj).</summary>
public static partial class HandViewer
{
    /// <summary>Show this teammate's hand, or nobody's (auto-test helper; same as clicking their eye).</summary>
    internal static void View(ulong? netId)
    {
        _viewed.Clear();
        if (netId is ulong id) _viewed.Add(id);
        _viewChosen = true;
    }

    /// <summary>The top shown hand (most recently clicked): what the single-hand checks look at (auto-test).</summary>
    private static ShownHand? TopHand => _hands.Count > 0 ? _hands[^1] : null;

    private static List<(CardModel Model, NHandCardHolder Holder)> TopCards => TopHand?.Cards ?? new();

    /// <summary>
    /// Shown hands, bottom first: owner, layer index, how far its cards actually sit above the native fan (layer
    /// units, averaged over the cards not hovered or dragged), and k × "% of card visible" of a card (auto-test).
    /// </summary>
    internal static List<(ulong NetId, int LayerIndex, float Raise, float Expected)> StackedHands()
    {
        var result = new List<(ulong, int, float, float)>();
        for (int k = 0; k < _hands.Count; k++)
        {
            ShownHand h = _hands[k];
            if (!GodotObject.IsInstanceValid(h.Layer) || PileViewer.Teammates().FirstOrDefault(t => t.player.NetId == h.NetId).player is not Player p) continue;
            var pile = PileType.Hand.GetPile(p).Cards.ToList();
            var raises = h.Cards.Where(c => GodotObject.IsInstanceValid(c.Holder) && c.Holder != _focused && c.Holder != _drag && pile.Contains(c.Model))
                .Select(c => HandPosHelper.GetPosition(pile.Count, pile.IndexOf(c.Model)).Y - c.Holder.Position.Y).ToList();
            float expected = (k + 1) * Settings.HandVisible * CardHeight * HandPosHelper.GetScale(Math.Max(pile.Count, 1)).Y;
            result.Add((h.NetId, h.Layer.GetIndex(), raises.Count > 0 ? raises.Average() : float.NaN, expected));
        }
        return result;
    }

    /// <summary>Hover state for the auto-test: hovered card, closeup shown, tooltips held back, input blocked.</summary>
    internal static string DescribeHover() =>
        $"hovered={Name(_focused)} closeup={Name(_lastCloseup)} tipsHeld={_tipsHeld} session={_hoverActive} blocked={_inputBlocked}";

    private static string Name(NHandCardHolder? h) =>
        h != null && GodotObject.IsInstanceValid(h) ? h.CardNode?.Model?.Id.Entry ?? "?" : "none";

    /// <summary>Index (in the shown teammate hand) of the first card matching, or -1 (auto-test).</summary>
    internal static int IndexOf(Func<CardModel, bool> match) => TopCards.FindIndex(s => match(s.Model));

    /// <summary>Index of the matching card nearest the middle of the shown teammate hand, or -1 (auto-test).</summary>
    internal static int MiddleIndexOf(Func<CardModel, bool> match) => Enumerable.Range(0, TopCards.Count)
        .Where(i => match(TopCards[i].Model)).OrderBy(i => Math.Abs(i - (TopCards.Count - 1) / 2.0)).DefaultIfEmpty(-1).First();

    /// <summary>The dragged card's cost and text (auto-test).</summary>
    internal static string DescribeDragCard() => _drag?.CardNode is NCard node ? DescribeCardNode(node) : "no drag";

    internal static bool IsDragging => _drag != null;

    internal static bool IsAiming => _targeting;

    /// <summary>Teammate cards shown, in hand order: model, the cost label drawn on the card, its glow (auto-test).</summary>
    internal static List<(CardModel Model, string Cost, string Glow)> ShownCards()
    {
        if (TopHand?.NetId is not ulong id || PileViewer.Teammates().FirstOrDefault(t => t.player.NetId == id).player is not Player p) return new();
        return PileType.Hand.GetPile(p).Cards.Select(c => TopCards.FirstOrDefault(s => s.Model == c))
            .Where(s => s.Holder != null && GodotObject.IsInstanceValid(s.Holder))
            .Select(s => (s.Model, s.Holder.CardNode?.GetNodeOrNull<Label>("%EnergyLabel")?.Text ?? "?", DescribeGlow(s.Holder.CardNode))).ToList();
    }

    internal static string? HoveredId => _focused != null && GodotObject.IsInstanceValid(_focused) ? _focused.CardNode?.Model?.Id.Entry : null;

    internal static string? CloseupId => _lastCloseup != null && GodotObject.IsInstanceValid(_lastCloseup) ? _lastCloseup.CardNode?.Model?.Id.Entry : null;

    internal static bool InputBlocked => _inputBlocked;

    /// <summary>Closeup's bottom edge and the top edge of your cards under it (viewport units), and its brightness (auto-test).</summary>
    internal static (float Bottom, float OwnTop, float Modulate)? CloseupGeometry()
    {
        if (_lastCloseup == null || !GodotObject.IsInstanceValid(_lastCloseup)) return null;
        // Your cards that are actually under the closeup (overlap it horizontally).
        Rect2 rect = _lastCloseup.Hitbox.GetGlobalRect();
        float ownTop = NPlayerHand.Instance?.ActiveHolders.Select(h => h.Hitbox.GetGlobalRect())
            .Where(r => r.End.X > rect.Position.X && r.Position.X < rect.End.X)
            .Select(r => r.Position.Y).DefaultIfEmpty(float.NaN).Min() ?? float.NaN;
        return (rect.End.Y, ownTop, _lastCloseup.Modulate.R);
    }

    /// <summary>Glow actually shown on a card node: "-" (none) or playable/gold/red (auto-test).</summary>
    internal static string DescribeGlow(NCard? node)
    {
        if (node?.CardHighlight is not NCardHighlight hl || hl.Material is not ShaderMaterial m) return "?";
        if (m.GetShaderParameter("width").AsSingle() < 0.04f) return "-";
        Color c = hl.Modulate;
        return c == NCardHighlight.red ? "red" : c == NCardHighlight.gold ? "gold" : "playable";
    }

    /// <summary>"Title=glow" for each teammate card shown, in hand order (auto-test).</summary>
    internal static string DescribeTeammateGlow() => string.Join(", ", TopCards
        .Where(s => GodotObject.IsInstanceValid(s.Holder))
        .Select(s => $"{s.Model.Title}={DescribeGlow(s.Holder.CardNode)}"));

    /// <summary>"Title=glow" for your own native hand, in hand order (auto-test reference).</summary>
    internal static string DescribeOwnGlow() => NPlayerHand.Instance is NPlayerHand h
        ? string.Join(", ", h.ActiveHolders.Select(x => $"{x.CardNode?.Model?.Title}={DescribeGlow(x.CardNode)}"))
        : "no hand";

    /// <summary>Your own hand's look (auto-test): first card's body part colors.</summary>
    internal static string DescribeOwnHandLook() => NPlayerHand.Instance is NPlayerHand h
        ? $"hand alpha={h.CardHolderContainer.Modulate.A:0.##} hitboxes={string.Join("/", h.ActiveHolders.Select(x => x.Hitbox.MouseFilter.ToString()[0]))} teammate shade={(TopHand is ShownHand top && GodotObject.IsInstanceValid(top.Layer) ? top.Layer.Modulate.R.ToString("0.##") : "-")}"
        : "no hand";

    /// <summary>"ID|cost|description" for one rendered card (auto-test comparisons).</summary>
    internal static string DescribeCardNode(NCard? card) => card == null ? "?" :
        $"{card.Model?.Id.Entry}|cost={card.GetNodeOrNull<Label>("%EnergyLabel")?.Text}" +
        $"|ench={(card.GetNodeOrNull<Control>("%Enchantment") is Control e && e.Visible ? e.GetNodeOrNull<Label>("Label")?.Text ?? "?" : "-")}" +
        $"|{card.GetNodeOrNull<RichTextLabel>("%DescriptionLabel")?.Text?.Replace('\n', ' ')}";

    /// <summary>How the viewer currently renders the teammate's hand, one card per entry, in hand order (auto-test).</summary>
    internal static List<string> DumpTeammateCards() =>
        TopCards.Where(s => GodotObject.IsInstanceValid(s.Holder)).Select(s => DescribeCardNode(s.Holder.CardNode)).ToList();

    /// <summary>How the game renders your own hand, in hand order (auto-test reference for the teammate's view).</summary>
    internal static List<string> DumpOwnHand() => NPlayerHand.Instance is NPlayerHand h
        ? h.ActiveHolders.Select(x => DescribeCardNode(x.CardNode)).ToList()
        : new List<string>();

    /// <summary>Global center of teammate card #index, e.g. to point the mouse at it (auto-test).</summary>
    /// <summary>Middle of the part of a teammate card that peeks out above your hand (auto-test mouse target).</summary>
    internal static Vector2? PeekPoint(int index)
    {
        if (index >= TopCards.Count || !GodotObject.IsInstanceValid(TopCards[index].Holder)) return null;
        Rect2 rect = TopCards[index].Holder.Hitbox.GetGlobalRect();
        return new Vector2(rect.GetCenter().X, rect.Position.Y + rect.Size.Y * Mathf.Max(Settings.HandVisible, 0.1f) * 0.5f);
    }

    internal static Vector2? CardCenter(int index) =>
        index < TopCards.Count && GodotObject.IsInstanceValid(TopCards[index].Holder)
            ? TopCards[index].Holder.Hitbox.GetGlobalRect().GetCenter() : null;

    /// <summary>Closeup vs your hand: its bottom edge, your hand's top edge, whether the mouse is on it (auto-test).</summary>
    internal static string DescribeCloseup()
    {
        if (_focused == null || !GodotObject.IsInstanceValid(_focused)) return "no closeup";
        Rect2 rect = _focused.Hitbox.GetGlobalRect();
        float ownTop = NPlayerHand.Instance?.ActiveHolders.Select(h => h.Hitbox.GetGlobalRect().Position.Y).DefaultIfEmpty(float.NaN).Min() ?? float.NaN;
        return $"closeup top={rect.Position.Y:0} bottom={rect.End.Y:0} ownHandTop={ownTop:0} mouseOnIt={rect.HasPoint(LastMouse)} modulate={_focused.Modulate.R:0.##}";
    }

    /// <summary>The closeup card, if any (auto-test).</summary>
    internal static string DescribeFocus() => _focused != null && GodotObject.IsInstanceValid(_focused)
        ? $"{_focused.CardNode?.Model?.Id.Entry} scale={_focused.Scale.X:0.##} rot={_focused.RotationDegrees:0.#} pos=({_focused.Position.X:0},{_focused.Position.Y:0}) z={_focused.ZIndex}"
        : "none";

    /// <summary>Teammate cards currently drawn (auto-test).</summary>
    internal static string DescribeCards() => TopHand is not ShownHand top || !GodotObject.IsInstanceValid(top.Layer)
        ? "no layer"
        : $"layer visible={top.Layer.Visible} index={top.Layer.GetIndex()} cards=" + string.Join(", ", TopCards.Select(s =>
            $"{s.Model.Id.Entry}@({s.Holder.Position.X:0},{s.Holder.Position.Y:0}) cost={s.Holder.CardNode?.GetNodeOrNull<Label>("%EnergyLabel")?.Text}"));

    /// <summary>Your own hand's Hand Card Count label offsets (auto-test).</summary>
    internal static string DescribeIndexLabels() => NPlayerHand.Instance is NPlayerHand h
        ? string.Join(", ", h.ActiveHolders.Select(x => x.GetNodeOrNull<Control>("HandIndex")?.Position.Y.ToString("0") ?? "?"))
        : "no hand";

    internal static Button? HideButton => _hideButton != null && GodotObject.IsInstanceValid(_hideButton) ? _hideButton : null;

    /// <summary>One-line summary for logs and the auto-test.</summary>
    internal static string DescribeState()
    {
        string button = HideButton is Button b ? $"button '{b.Text}' visible={b.Visible} at {b.GlobalPosition}" : "no button";
        return $"viewing={DescribeViewed()} hideOwnHand={HideOwnHand} {button}";
    }
}
