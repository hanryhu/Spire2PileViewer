using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
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
        _lastText = "";
        ClearCards();
        if (_cardLayer != null && GodotObject.IsInstanceValid(_cardLayer)) _cardLayer.QueueFree();
        _cardLayer = null;
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
        UpdateText(viewed);
        UpdateCards(viewed);
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

    /// <summary>The hv3 text readout, now only logged (the cards themselves are drawn since hv4).</summary>
    private static void UpdateText(Player? viewed)
    {
        string text = viewed == null ? "" : DescribeHand(viewed);
        if (text == _lastText) return;
        _lastText = text;
        if (text != "") ModLog.Info("HandViewer: hand text: " + text);
    }

    internal static string CurrentText => _lastText;

    // ---- hv4: the teammate's hand, drawn behind yours ----

    // Holder hitbox height in the hand scene (probe hv1): a card is 422 units tall before the hand's scale.
    private const float CardHeight = 422f;

    /// <summary>Sibling of NPlayerHand.CardHolderContainer, placed just before it so it draws behind your hand.</summary>
    private static Control? _cardLayer;

    private static readonly List<(CardModel Model, NHandCardHolder Holder)> _shown = new();

    private static ulong? _shownFor;

    /// <summary>Teammate card under the mouse (native closeup), or null.</summary>
    private static NHandCardHolder? _focused;

    private static double _nextVisualRefresh;

    /// <summary>How far (container units) the teammate's cards sit above yours: "% of card visible" of a card.</summary>
    private static float RaiseFor(int handSize) => Settings.HandVisible * CardHeight * HandPosHelper.GetScale(Math.Max(handSize, 1)).Y;

    private static void UpdateCards(Player? viewed)
    {
        NPlayerHand? hand = NPlayerHand.Instance;
        bool show = viewed != null && hand != null && NCapstoneContainer.Instance?.InUse != true;
        if (!show)
        {
            if (_cardLayer != null && GodotObject.IsInstanceValid(_cardLayer)) _cardLayer.Visible = false;
            if (viewed == null) ClearCards();
            return;
        }

        Control container = hand!.CardHolderContainer;
        if (_cardLayer == null || !GodotObject.IsInstanceValid(_cardLayer))
        {
            _shown.Clear();
            _cardLayer = new Control { Name = "Spire2PileViewer_TeammateHand", MouseFilter = Control.MouseFilterEnum.Ignore };
            Node parent = container.GetParent();
            parent.AddChild(_cardLayer);
            parent.MoveChild(_cardLayer, container.GetIndex()); // just before → drawn behind your cards
            ModLog.Info($"HandViewer: card layer added under {parent.Name} at index {_cardLayer.GetIndex()}");
        }
        _cardLayer.Visible = true;
        _cardLayer.Position = container.Position;
        _cardLayer.Size = container.Size;
        _cardLayer.Scale = container.Scale;
        float shade = 1f - 0.6f * Settings.HandShadow; // 0 → untinted, 1 → dark like a heavy shadow
        _cardLayer.Modulate = new Color(shade, shade, shade);

        if (_shownFor != viewed!.NetId) ClearCards();
        _shownFor = viewed.NetId;

        IReadOnlyList<CardModel> cards = PileType.Hand.GetPile(viewed).Cards;
        // Drop cards that left the hand, add new ones.
        for (int i = _shown.Count - 1; i >= 0; i--)
        {
            if (!cards.Contains(_shown[i].Model))
            {
                if (GodotObject.IsInstanceValid(_shown[i].Holder)) _shown[i].Holder.QueueFree();
                _shown.RemoveAt(i);
            }
        }
        foreach (CardModel card in cards)
        {
            if (_shown.Any(s => s.Model == card)) continue;
            NCard? node = NCard.Create(card);
            if (node == null) continue;
            NHandCardHolder holder = NHandCardHolder.Create(node, hand);
            _cardLayer.AddChild(holder);
            holder.SetIndexLabel(0); // no Hand Card Count numbers on the teammate's cards
            int index = cards.ToList().IndexOf(card);
            holder.Position = HandPosHelper.GetPosition(cards.Count, index) - new Vector2(0f, RaiseFor(cards.Count));
            holder.SetScaleInstantly(HandPosHelper.GetScale(cards.Count));
            holder.SetAngleInstantly(HandPosHelper.GetAngle(cards.Count, index));
            holder.HolderFocused += h => { _focused = h; ModLog.Info($"HandViewer: closeup {h.CardNode?.Model?.Id.Entry}"); };
            holder.HolderUnfocused += h => { if (_focused == h) _focused = null; };
            _shown.Add((card, holder));
        }

        // Native fan layout for the teammate's hand size, raised so only "% of card visible" peeks out above yours.
        // A hovered card gets the native closeup (NPlayerHand.RefreshLayout): upright, full size, lifted so the
        // whole card shows, drawn above your hand (the holder sets ZIndex 1 on hover), neighbours slide aside.
        float raise = RaiseFor(cards.Count);
        if (_focused != null && !GodotObject.IsInstanceValid(_focused)) _focused = null;
        int focusIndex = _focused == null ? -1 : _shown.FindIndex(s => s.Holder == _focused) is int f && f >= 0
            ? cards.ToList().IndexOf(_shown[f].Model) : -1;
        for (int i = 0; i < cards.Count; i++)
        {
            var entry = _shown.FirstOrDefault(s => s.Model == cards[i]);
            if (entry.Holder == null || !GodotObject.IsInstanceValid(entry.Holder)) continue;
            NHandCardHolder holder = entry.Holder;
            _cardLayer.MoveChild(holder, i);
            Vector2 position = HandPosHelper.GetPosition(cards.Count, i) - new Vector2(0f, raise);
            if (focusIndex > -1 && i != focusIndex)
            {
                float push = Mathf.Lerp(100f, 0f, Mathf.Min(1f, Mathf.Abs(focusIndex - i) / 4f));
                position += Vector2.Left * Mathf.Sign(focusIndex - i) * push;
            }
            if (i == focusIndex)
            {
                position.Y = -holder.Hitbox.Size.Y * 0.5f + 2f;
                holder.SetAngleInstantly(0f);
                holder.SetScaleInstantly(Vector2.One);
                holder.Position = new Vector2(holder.Position.X, position.Y);
                holder.SetTargetPosition(position);
            }
            else
            {
                holder.SetTargetPosition(position);
                holder.SetTargetAngle(HandPosHelper.GetAngle(cards.Count, i));
                holder.SetTargetScale(HandPosHelper.GetScale(cards.Count));
            }
        }
        // The closeup is shown at full brightness; the rest keep the shadow tint.
        foreach (var (_, holder) in _shown)
        {
            if (!GodotObject.IsInstanceValid(holder)) continue;
            Color target = holder == _focused ? new Color(1f / shade, 1f / shade, 1f / shade) : Colors.White;
            if (holder.Modulate != target) holder.Modulate = target;
        }

        // The game only refreshes card nodes it can find (NCard.FindOnTable), which excludes ours — refresh
        // costs/numbers ourselves a few times a second.
        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _nextVisualRefresh)
        {
            _nextVisualRefresh = now + 0.25;
            foreach (var (_, holder) in _shown)
            {
                if (GodotObject.IsInstanceValid(holder)) holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            }
        }
    }

    private static void ClearCards()
    {
        foreach (var (_, holder) in _shown)
        {
            if (GodotObject.IsInstanceValid(holder)) holder.QueueFree();
        }
        _shown.Clear();
        _shownFor = null;
        _focused = null;
    }

    /// <summary>"ID|cost|description" for one rendered card (auto-test comparisons).</summary>
    internal static string DescribeCardNode(NCard? card) => card == null ? "?" :
        $"{card.Model?.Id.Entry}|cost={card.GetNodeOrNull<Label>("%EnergyLabel")?.Text}" +
        $"|ench={(card.GetNodeOrNull<Control>("%Enchantment") is Control e && e.Visible ? e.GetNodeOrNull<Label>("Label")?.Text ?? "?" : "-")}" +
        $"|{card.GetNodeOrNull<RichTextLabel>("%DescriptionLabel")?.Text?.Replace('\n', ' ')}";

    /// <summary>How the viewer currently renders the teammate's hand, one card per entry, in hand order (auto-test).</summary>
    internal static List<string> DumpTeammateCards() =>
        _shown.Where(s => GodotObject.IsInstanceValid(s.Holder)).Select(s => DescribeCardNode(s.Holder.CardNode)).ToList();

    /// <summary>How the game renders your own hand, in hand order (auto-test reference for the teammate's view).</summary>
    internal static List<string> DumpOwnHand() => NPlayerHand.Instance is NPlayerHand h
        ? h.ActiveHolders.Select(x => DescribeCardNode(x.CardNode)).ToList()
        : new List<string>();

    /// <summary>Global center of teammate card #index, e.g. to point the mouse at it (auto-test).</summary>
    internal static Vector2? CardCenter(int index) =>
        index < _shown.Count && GodotObject.IsInstanceValid(_shown[index].Holder)
            ? _shown[index].Holder.Hitbox.GetGlobalRect().GetCenter() : null;

    /// <summary>The closeup card, if any (auto-test).</summary>
    internal static string DescribeFocus() => _focused != null && GodotObject.IsInstanceValid(_focused)
        ? $"{_focused.CardNode?.Model?.Id.Entry} scale={_focused.Scale.X:0.##} rot={_focused.RotationDegrees:0.#} pos=({_focused.Position.X:0},{_focused.Position.Y:0}) z={_focused.ZIndex}"
        : "none";

    /// <summary>Teammate cards currently drawn (auto-test).</summary>
    internal static string DescribeCards() => _cardLayer == null || !GodotObject.IsInstanceValid(_cardLayer)
        ? "no layer"
        : $"layer visible={_cardLayer.Visible} index={_cardLayer.GetIndex()} cards=" + string.Join(", ", _shown.Select(s =>
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
        return $"viewing={ViewedNetId?.ToString() ?? "nobody"} hideOwnHand={HideOwnHand} {button}";
    }
}
