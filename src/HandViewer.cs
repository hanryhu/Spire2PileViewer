using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
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

    /// <summary>Hotkey: toggle teammate #index's hand view (0-based among PileViewer.Teammates()).</summary>
    internal static bool ToggleTeammate(int teammateIndex)
    {
        if (!Settings.HandViewerEnabled) return false;
        var teammates = PileViewer.Teammates();
        if (teammateIndex >= teammates.Count) return false;
        Toggle(teammates[teammateIndex].player);
        return true;
    }

    /// <summary>Hotkey: hide/show your own hand (only while the Hide hand feature is on).</summary>
    internal static bool ToggleHideOwnHandHotkey()
    {
        if (!Settings.HandViewerEnabled || !Settings.HideHandButtonEnabled) return false;
        ToggleHideOwnHand();
        return true;
    }

    public static void ToggleHideOwnHand()
    {
        HideOwnHand = !HideOwnHand;
        if (!HideOwnHand) ApplyHiddenLook(false);
        ModLog.Info($"HandViewer: hide own hand = {HideOwnHand}");
    }

    /// <summary>Combat over / leaving: drop all view state and the button.</summary>
    public static void Reset()
    {
        if (HideOwnHand) ApplyHiddenLook(false);
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
        ApplyHiddenLook(HideOwnHand);
        UpdateCards(viewed);
    }

    private static void UpdateHideButton(NCombatRoom room)
    {
        Control? energy = room.Ui?.EnergyCounterContainer;
        bool show = Settings.HideHandButtonEnabled && energy != null && energy.IsVisibleInTree()
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

        // Default: centered just above the energy counter (left side); plus the player's drag offset.
        Rect2 rect = EnergyRect(energy!);
        Vector2 size = _hideButton.Size;
        _hideButton.GlobalPosition = new Vector2(rect.Position.X + (rect.Size.X - size.X) / 2f, rect.Position.Y - size.Y - 8f)
            + Settings.HideButtonOffset;
    }

    /// <summary>On-screen rect of the energy counter (the container itself can be zero-sized; use its children).</summary>
    private static Rect2 EnergyRect(Control container)
    {
        Rect2 rect = container.GetGlobalRect();
        foreach (Node child in container.GetChildren())
        {
            if (child is Control c && c.Visible && c.Size != Vector2.Zero) rect = rect.Size == Vector2.Zero ? c.GetGlobalRect() : rect.Merge(c.GetGlobalRect());
        }
        return rect;
    }

    // Drag: a plain drag moves the button (saved); a click without movement toggles the hand.
    private const float DragThreshold = 6f;

    private static bool _buttonDown;

    private static bool _buttonDragged;

    private static Vector2 _buttonDragStartMouse;

    private static Vector2 _buttonDragStartOffset;

    internal static void HandleHideButtonInput(Button button, InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _buttonDown = true;
                _buttonDragged = false;
                _buttonDragStartMouse = mb.GlobalPosition;
                _buttonDragStartOffset = Settings.HideButtonOffset;
            }
            else
            {
                _buttonDown = false;
                if (_buttonDragged)
                {
                    _buttonDragged = false;
                    Settings.Save();
                    ModLog.Info($"HandViewer: Hide hand button moved, offset {Settings.HideButtonOffset}");
                    button.AcceptEvent(); // a drag is not a click
                }
            }
        }
        else if (ev is InputEventMouseMotion mm && _buttonDown)
        {
            Vector2 delta = mm.GlobalPosition - _buttonDragStartMouse;
            if (_buttonDragged || delta.Length() > DragThreshold)
            {
                _buttonDragged = true;
                Settings.HideButtonOffset = _buttonDragStartOffset + delta;
                button.AcceptEvent();
            }
        }
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
            TooltipText = "Hide your own hand to see your teammate's hand behind it.\nDrag to move (reset in the F1 menu).",
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
        button.GuiInput += ev => HandleHideButtonInput(button, ev);
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

    // hv10: glow last applied to each shown holder (null = no glow), so the highlight only animates on change.
    private static readonly Dictionary<NHandCardHolder, Color?> _glow = new();

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
        // 0 → untinted, 1 → dark like a heavy shadow; a separate (lighter by default) amount while your hand is hidden.
        float shade = 1f - 0.6f * (HideOwnHand ? Settings.HandShadowWhenHidden : Settings.HandShadow);
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
                _glow.Remove(_shown[i].Holder);
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

        // The game only refreshes card nodes it can find (NCard.FindOnTable) and holders inside its own hand
        // container, which excludes ours — refresh costs/numbers and the playable glow a few times a second.
        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _nextVisualRefresh)
        {
            _nextVisualRefresh = now + 0.25;
            foreach (var (model, holder) in _shown)
            {
                if (!GodotObject.IsInstanceValid(holder) || holder.CardNode is not NCard node) continue;
                node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
                ApplyGlow(holder, node, TeammateGlow(model, viewed));
            }
        }
    }

    /// <summary>
    /// hv10: the glow the teammate would see on this card in their own hand (NHandCardHolder.UpdateCard: red, gold,
    /// or playable when CardModel.CanPlay, which already uses the owner's energy/stars and hooks), plus their turn
    /// state: no glow once they've pressed End Turn, outside their play phase, or while others take an extra turn.
    /// (Natively your own cards keep glowing after End Turn but your whole hand drops away; a teammate's hand has
    /// no such cue, so the glow carries it.) Null means no glow.
    /// </summary>
    internal static Color? TeammateGlow(CardModel card, Player owner)
    {
        CombatManager combat = CombatManager.Instance;
        if (!combat.IsInProgress || owner.PlayerCombatState?.Phase != PlayerTurnPhase.Play) return null;
        if (combat.IsPlayerReadyToEndTurn(owner)) return null;
        var extra = combat.PlayersTakingExtraTurn;
        if (extra.Count > 0 && !extra.Contains(owner)) return null;
        if (card.ShouldGlowRed) return NCardHighlight.red;
        if (!card.CanPlay()) return null;
        return card.ShouldGlowGold ? NCardHighlight.gold : NCardHighlight.playableColor;
    }

    private static void ApplyGlow(NHandCardHolder holder, NCard node, Color? glow)
    {
        if (_glow.TryGetValue(holder, out Color? applied) && applied == glow) return;
        _glow[holder] = glow;
        if (glow is Color color)
        {
            node.CardHighlight.AnimShow();
            node.CardHighlight.Modulate = color;
        }
        else
        {
            node.CardHighlight.AnimHide();
        }
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
    internal static string DescribeTeammateGlow() => string.Join(", ", _shown
        .Where(s => GodotObject.IsInstanceValid(s.Holder))
        .Select(s => $"{s.Model.Title}={DescribeGlow(s.Holder.CardNode)}"));

    /// <summary>"Title=glow" for your own native hand, in hand order (auto-test reference).</summary>
    internal static string DescribeOwnGlow() => NPlayerHand.Instance is NPlayerHand h
        ? string.Join(", ", h.ActiveHolders.Select(x => $"{x.CardNode?.Model?.Title}={DescribeGlow(x.CardNode)}"))
        : "no hand";

    private static void ClearCards()
    {
        foreach (var (_, holder) in _shown)
        {
            if (GodotObject.IsInstanceValid(holder)) holder.QueueFree();
        }
        _shown.Clear();
        _glow.Clear();
        _shownFor = null;
        _focused = null;
    }

    // ---- hv7: hide your own hand ----

    private static bool _hiddenApplied;

    /// <summary>
    /// Hidden: your hand's card container is fully transparent and its cards ignore the mouse (so nothing is clicked
    /// by accident), leaving only the teammate's shadowed hand. Re-applied every frame because the native layout
    /// resets the hitboxes. Unhiding restores the native look and input.
    /// </summary>
    private static void ApplyHiddenLook(bool hidden)
    {
        if (!hidden && !_hiddenApplied) return;
        if (NPlayerHand.Instance is not NPlayerHand hand) return;
        Control container = hand.CardHolderContainer;
        container.Modulate = hidden ? new Color(1f, 1f, 1f, 0f) : Colors.White;
        foreach (NHandCardHolder holder in hand.ActiveHolders)
        {
            holder.Hitbox.MouseFilter = hidden ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        }
        _hiddenApplied = hidden;
    }

    /// <summary>Your own hand's look (auto-test): first card's body part colors.</summary>
    internal static string DescribeOwnHandLook() => NPlayerHand.Instance is NPlayerHand h
        ? $"hand alpha={h.CardHolderContainer.Modulate.A:0.##} hitboxes={string.Join("/", h.ActiveHolders.Select(x => x.Hitbox.MouseFilter.ToString()[0]))} teammate shade={(_cardLayer != null && GodotObject.IsInstanceValid(_cardLayer) ? _cardLayer.Modulate.R.ToString("0.##") : "-")}"
        : "no hand";

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
