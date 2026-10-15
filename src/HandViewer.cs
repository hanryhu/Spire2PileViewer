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
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>
/// Hand Viewer (see HAND_VIEWER.md): shows teammates' hands behind your own (up to Max Hand Viewers, stacked).
/// This class owns the view state (whose hands are shown, whether your own hand is hidden) and the "Hide hand"
/// button under End Turn. Driven from PileViewer's per-frame update.
/// </summary>
public static partial class HandViewer
{
    /// <summary>
    /// Teammates whose hands are shown, least recently clicked first: that one sits lowest (just above your hand),
    /// the most recently clicked on top. Never more than Settings.MaxHandViewers; showing another closes the oldest.
    /// </summary>
    private static readonly List<ulong> _viewed = new();

    public static IReadOnlyList<ulong> ViewedNetIds => _viewed;

    public static bool IsViewing(ulong netId) => _viewed.Contains(netId);

    /// <summary>The most recently shown teammate's hand (the top one), or null.</summary>
    public static ulong? ViewedNetId => _viewed.Count > 0 ? _viewed[^1] : null;

    /// <summary>
    /// The player picked a hand (or closed it) this run. Until then the lowest-numbered teammate's hand is shown.
    /// The choice carries over between fights and is forgotten on returning to the main menu.
    /// </summary>
    private static bool _viewChosen;

    /// <summary>Your own hand is hidden so the teammate's hand is easier to see. Reset at the start of each fight.</summary>
    public static bool HideOwnHand { get; private set; }

    private static Button? _hideButton;

    private static StyleBoxFlat? _hideButtonStyle;

    /// <summary>
    /// Strip "Hand" slot: show this teammate's hand on top of the stack, or hide it if it's already shown. Past
    /// Max Hand Viewers the least recently clicked hand closes. On your own strip it hides/shows your own hand
    /// instead, like the Hide hand button.
    /// </summary>
    public static void Toggle(Player player)
    {
        if (!Settings.HandViewerEnabled) return;
        if (player.NetId == LocalNetId)
        {
            ToggleHideOwnHand();
            return;
        }
        if (!_viewed.Remove(player.NetId)) _viewed.Add(player.NetId);
        TrimToMax();
        _viewChosen = true;
        ModLog.Info($"HandViewer: viewing {DescribeViewed()}");
    }

    private static void TrimToMax()
    {
        while (_viewed.Count > Settings.MaxHandViewers) _viewed.RemoveAt(0);
    }

    internal static string DescribeViewed() => _viewed.Count == 0 ? "nobody" : string.Join(",", _viewed);

    private static ulong LocalNetId => RunManager.Instance?.NetService?.NetId ?? 0;

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
        if (!Settings.HandViewerEnabled) return false; // works with the button hidden too
        ToggleHideOwnHand();
        return true;
    }

    public static void ToggleHideOwnHand()
    {
        HideOwnHand = !HideOwnHand;
        if (!HideOwnHand) ApplyHiddenLook(false);
        ModLog.Info($"HandViewer: hide own hand = {HideOwnHand}");
    }

    /// <summary>
    /// Combat over: drop the cards, the button and "hide my hand" (so every fight starts with your hand shown).
    /// Whose hand is shown carries over to the next fight.
    /// </summary>
    public static void Reset()
    {
        if (HideOwnHand) ApplyHiddenLook(false);
        HideOwnHand = false;
        if (_hideButton != null && GodotObject.IsInstanceValid(_hideButton)) _hideButton.QueueFree();
        _hideButton = null;
        _lastText = "";
        ClearCards();
    }

    /// <summary>Back at the main menu: forget whose hands were shown, so the next run starts with the default again.</summary>
    public static void ResetRun()
    {
        Reset();
        _viewed.Clear();
        _viewChosen = false;
    }

    /// <summary>Per-frame update while in combat (called by PileViewer).</summary>
    internal static void Update(NCombatRoom room, Func<ulong, Player?> findPlayer)
    {
        if (!Settings.ReceivesCardSignals && _signals.Count > 0) StopSignals(); // turned off while one was blinking
        if (!Settings.HandViewerEnabled)
        {
            if (_hands.Count > 0 || HideOwnHand || _hideButton != null) Reset();
            return;
        }
        if (!_viewChosen)
        {
            // Default: the lowest-numbered teammates, as many as Max Hand Viewers allows (lowest number at the bottom).
            var defaults = PileViewer.Teammates().Take(Settings.MaxHandViewers).Select(t => t.player.NetId).ToList();
            if (!defaults.SequenceEqual(_viewed))
            {
                _viewed.Clear();
                _viewed.AddRange(defaults);
                ModLog.Info($"HandViewer: viewing {DescribeViewed()} (default)");
            }
        }
        TrimToMax();
        _viewed.RemoveAll(id => findPlayer(id) == null); // teammate left / not in this combat
        List<Player> viewed = _viewed.Select(id => findPlayer(id)!).ToList();
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
            TooltipText = "Drag to move",
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
    private static void UpdateText(List<Player> viewed)
    {
        string text = string.Join(" | ", viewed.Select(DescribeHand));
        if (text == _lastText) return;
        _lastText = text;
        if (text != "") ModLog.Info("HandViewer: hand text: " + text);
    }

    internal static string CurrentText => _lastText;

    // ---- hv4: the teammate's hand, drawn behind yours ----

    // Holder hitbox height in the hand scene (probe hv1): a card is 422 units tall before the hand's scale.
    private const float CardHeight = 422f;

    /// <summary>
    /// One shown teammate hand: its layer (a sibling of NPlayerHand.CardHolderContainer, placed before it so it
    /// draws behind your hand) and the card holders on it.
    /// </summary>
    private sealed class ShownHand
    {
        public required ulong NetId;

        public required Control Layer;

        public readonly List<(CardModel Model, NHandCardHolder Holder)> Cards = new();
    }

    /// <summary>Shown hands, bottom (least recently clicked, drawn in front) first.</summary>
    private static readonly List<ShownHand> _hands = new();

    /// <summary>Teammate card we told the game we're hovering (shown to others over your head), or null.</summary>
    private static CardModel? _syncedHover;

    /// <summary>
    /// Like hovering your own card: once a teammate card's closeup shows (not during the hover delay), the game shows
    /// it to the others over your head.
    /// </summary>
    private static void SyncHover(CardModel? model)
    {
        if (!Settings.SendsCardSignals) model = null;
        if (model == _syncedHover) return;
        var tracker = RunManager.Instance?.HoveredModelTracker;
        if (tracker == null) return;
        if (model != null) tracker.OnLocalCardHovered(model);
        else tracker.OnLocalCardUnhovered();
        _syncedHover = model;
    }

    /// <summary>Owner of the teammate card under the mouse or being dragged, or null.</summary>
    internal static ulong? HoveredOwnerNetId
    {
        get
        {
            NHandCardHolder? card = _drag ?? _focused;
            return card != null && GodotObject.IsInstanceValid(card) ? OwnerOf(card) : null;
        }
    }

    private static IEnumerable<NHandCardHolder> AllHolders =>
        _hands.SelectMany(h => h.Cards).Select(c => c.Holder).Where(h => GodotObject.IsInstanceValid(h));

    /// <summary>Teammate card under the mouse (native closeup), or null.</summary>
    private static NHandCardHolder? _focused;

    /// <summary>Where the closeup's bottom edge sits (layer units), fixed when the hover starts.</summary>
    private static float? _focusBottom;

    private static double _nextBubble;

    // Hover delay: when the hover began; the card clicked (shows its closeup at once); native hover effects held back.
    private static double _focusStart;

    private static NHandCardHolder? _clicked;

    private static bool _tipsHeld;

    // Once a closeup has shown, moving straight onto a neighbouring teammate card shows its closeup with no delay.
    // The hover "session" ends when no teammate card has been hovered for a moment.
    private static bool _hoverActive;

    private static double _lastUnfocus;

    /// <summary>Owner of the hand the current hover session is in.</summary>
    private static ulong? _sessionOwner;

    private static ulong? OwnerOf(NHandCardHolder? holder) =>
        holder == null ? null : _hands.FirstOrDefault(h => h.Cards.Any(c => c.Holder == holder))?.NetId;

    private const double HoverSessionGrace = 0.25;

    // Your own card play is in progress: teammate cards ignore the mouse so they don't get in its way.
    private static bool _inputBlocked;

    // Preview-only drag of a teammate card: pressed (not yet moved), being dragged, aiming with the game's arrow.
    private const float CardDragThreshold = 8f;

    private static NHandCardHolder? _pressed;

    private static Vector2 _pressMouse;

    private static NHandCardHolder? _drag;

    private static bool _targeting;

    private static bool _multiShown;

    private static NHandCardHolder? _lastCloseup;

    /// <summary>Mouse position (viewport units) from the last mouse event the game received.</summary>
    internal static Vector2 LastMouse;

    // hv10: glow last applied to each shown holder (null = no glow), so the highlight only animates on change.
    private static readonly Dictionary<NHandCardHolder, Color?> _glow = new();

    private static double _nextVisualRefresh;

    /// <summary>How far (container units) the teammate's cards sit above yours: "% of card visible" of a card.</summary>
    private static float RaiseFor(int handSize) => Settings.HandVisible * CardHeight * HandPosHelper.GetScale(Math.Max(handSize, 1)).Y;

    private static void UpdateCards(List<Player> viewed)
    {
        for (int i = _hands.Count - 1; i >= 0; i--)
        {
            if (!viewed.Any(p => p.NetId == _hands[i].NetId) || !GodotObject.IsInstanceValid(_hands[i].Layer)) RemoveHand(i);
        }
        NPlayerHand? hand = NPlayerHand.Instance;
        bool show = viewed.Count > 0 && hand != null && NCapstoneContainer.Instance?.InUse != true;
        if (!show)
        {
            foreach (ShownHand h in _hands) h.Layer.Visible = false;
            SyncHover(null);
            return;
        }

        // One layer per hand, in click order: the least recently clicked lowest, the most recent on top.
        Control container = hand!.CardHolderContainer;
        var ordered = viewed.Select(p => _hands.FirstOrDefault(h => h.NetId == p.NetId) ?? CreateHand(p.NetId, container)).ToList();
        _hands.Clear();
        _hands.AddRange(ordered);
        OrderLayers(container);

        double now = Time.GetTicksMsec() / 1000.0;
        BlockWhileYouPlay(hand);
        UpdateDrag();
        if (_focused != null && !GodotObject.IsInstanceValid(_focused)) _focused = null;
        NHandCardHolder? closeup = CloseupHolder(now);
        _lastCloseup = closeup;
        HoldBackHoverEffects(closeup);
        SyncHover(closeup != null && closeup != _drag ? closeup.CardNode?.Model : null);
        // 0 → untinted, 1 → dark like a heavy shadow; a separate (lighter by default) amount while your hand is hidden.
        float shade = 1f - 0.6f * (HideOwnHand ? Settings.HandShadowWhenHidden : Settings.HandShadow);
        // The game only refreshes card nodes it can find (NCard.FindOnTable) and holders inside its own hand
        // container, which excludes ours — refresh costs/numbers and the playable glow a few times a second.
        bool refresh = now >= _nextVisualRefresh;
        if (refresh) _nextVisualRefresh = now + 0.25;
        for (int k = 0; k < _hands.Count; k++) UpdateHand(_hands[k], viewed[k], k + 1, hand, container, shade, closeup, refresh);
    }

    // How far a closeup's bottom edge stays above the bottom of the screen (layer units).
    private const float ScreenMargin = 4f;

    /// <summary>The bottom of the screen in a hand layer's coordinates.</summary>
    private static float ScreenBottom(Control layer) =>
        (layer.GetGlobalTransformWithCanvas().AffineInverse() * new Vector2(0f, layer.GetViewportRect().Size.Y)).Y;

    private static ShownHand CreateHand(ulong netId, Control container)
    {
        var layer = new Control { Name = $"Spire2PileViewer_TeammateHand_{netId}", MouseFilter = Control.MouseFilterEnum.Ignore };
        Node parent = container.GetParent();
        parent.AddChild(layer);
        parent.MoveChild(layer, container.GetIndex()); // just before → drawn behind your cards
        ModLog.Info($"HandViewer: card layer for {netId} added under {parent.Name} at index {layer.GetIndex()}");
        return new ShownHand { NetId = netId, Layer = layer };
    }

    /// <summary>Layers right before your hand's container: the top hand first (drawn furthest back), the bottom last.</summary>
    private static void OrderLayers(Control container)
    {
        bool inOrder = true;
        for (int k = 0; k < _hands.Count; k++)
        {
            if (_hands[k].Layer.GetIndex() != container.GetIndex() - 1 - k) inOrder = false;
        }
        if (inOrder) return;
        Node parent = container.GetParent();
        for (int k = _hands.Count - 1; k >= 0; k--)
        {
            parent.MoveChild(_hands[k].Layer, parent.GetChildCount() - 1);
            parent.MoveChild(_hands[k].Layer, container.GetIndex());
        }
    }

    private static void RemoveHand(int index)
    {
        ShownHand shown = _hands[index];
        _hands.RemoveAt(index);
        bool Mine(NHandCardHolder? h) => h != null && shown.Cards.Any(c => c.Holder == h);
        if (Mine(_drag)) EndDrag(attempted: false);
        if (Mine(_focused)) _focused = null;
        if (Mine(_clicked)) _clicked = null;
        if (Mine(_pressed)) _pressed = null;
        foreach (var (_, holder) in shown.Cards) _glow.Remove(holder);
        if (GodotObject.IsInstanceValid(shown.Layer)) shown.Layer.QueueFree(); // frees its holders too
    }

    /// <summary>
    /// One teammate's hand: the native fan layout for their hand size, raised so "% of card visible" peeks out above
    /// the hand below it (level 1 above yours, level 2 above that, ...).
    /// </summary>
    private static void UpdateHand(ShownHand shown, Player viewed, int level, NPlayerHand hand, Control container, float shade,
        NHandCardHolder? closeup, bool refresh)
    {
        Control layer = shown.Layer;
        List<(CardModel Model, NHandCardHolder Holder)> held = shown.Cards;
        layer.Visible = true;
        layer.Position = container.Position;
        layer.Size = container.Size;
        layer.Scale = container.Scale;
        layer.Modulate = new Color(shade, shade, shade);

        IReadOnlyList<CardModel> cards = PileType.Hand.GetPile(viewed).Cards;
        float raise = level * RaiseFor(cards.Count);
        // Drop cards that left the hand, add new ones.
        for (int i = held.Count - 1; i >= 0; i--)
        {
            if (!cards.Contains(held[i].Model))
            {
                if (held[i].Holder == _drag) EndDrag(attempted: false);
                if (GodotObject.IsInstanceValid(held[i].Holder)) held[i].Holder.QueueFree();
                _glow.Remove(held[i].Holder);
                held.RemoveAt(i);
            }
        }
        foreach (CardModel card in cards)
        {
            if (held.Any(s => s.Model == card)) continue;
            NCard? node = NCard.Create(card);
            if (node == null) continue;
            NHandCardHolder holder = NHandCardHolder.Create(node, hand);
            layer.AddChild(holder);
            holder.SetIndexLabel(0); // no Hand Card Count numbers on the teammate's cards
            int index = cards.ToList().IndexOf(card);
            holder.Position = HandPosHelper.GetPosition(cards.Count, index) - new Vector2(0f, raise);
            holder.SetScaleInstantly(HandPosHelper.GetScale(cards.Count));
            holder.SetAngleInstantly(HandPosHelper.GetAngle(cards.Count, index));
            if (_inputBlocked) holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore;
            holder.HolderFocused += h =>
            {
                _focused = h;
                _focusStart = Time.GetTicksMsec() / 1000.0;
                _focusBottom = null;
                ModLog.Info($"HandViewer: hover {h.CardNode?.Model?.Id.Entry}");
            };
            holder.HolderUnfocused += h =>
            {
                if (_focused != h) return;
                _focused = null;
                _lastUnfocus = Time.GetTicksMsec() / 1000.0;
                _tipsHeld = false;
                if (_clicked == h) _clicked = null;
            };
            holder.HolderMouseClicked += h => OnTeammateCardPressed((NHandCardHolder)h);
            held.Add((card, holder));
        }

        // A hovered card gets the native closeup (NPlayerHand.RefreshLayout): upright, full size, lifted so the
        // whole card shows, drawn above every hand (the holder sets ZIndex 1 on hover), neighbours slide aside.
        int focusIndex = closeup == null || closeup == _drag ? -1 : held.FindIndex(s => s.Holder == closeup) is int f && f >= 0
            ? cards.ToList().IndexOf(held[f].Model) : -1;
        for (int i = 0; i < cards.Count; i++)
        {
            var entry = held.FirstOrDefault(s => s.Model == cards[i]);
            if (entry.Holder == null || !GodotObject.IsInstanceValid(entry.Holder)) continue;
            NHandCardHolder holder = entry.Holder;
            layer.MoveChild(holder, i);
            if (holder == _drag) continue; // the drag places it
            Vector2 position = HandPosHelper.GetPosition(cards.Count, i) - new Vector2(0f, raise);
            if (focusIndex > -1 && i != focusIndex)
            {
                float push = Mathf.Lerp(100f, 0f, Mathf.Min(1f, Mathf.Abs(focusIndex - i) / 4f));
                position += Vector2.Left * Mathf.Sign(focusIndex - i) * push;
            }
            if (i == focusIndex)
            {
                // Full size and upright. With nothing of yours to avoid (your hand hidden, or no cards: dead and
                // spectating, or an empty hand) the front (lowest) hand has nothing over it, so its card stays where
                // it is (moving it made it jitter under the mouse), only kept on screen; a hand further back is partly
                // covered by the ones in front, so its card rises like the native closeup. Otherwise lifted so the
                // whole card shows above your hand instead of covering it — its bottom edge where your hand starts,
                // but never above the mouse (an edge card hovered low, where your hand doesn't reach, stays under it).
                float height = holder.Hitbox.Size.Y;
                if (HideOwnHand || hand.ActiveHolders.Count == 0)
                {
                    if (level > 1) position.Y = -height * 0.5f + 2f;
                    position.Y = Mathf.Min(position.Y, ScreenBottom(layer) - height * 0.5f - ScreenMargin);
                }
                else
                {
                    float handTop = HandPosHelper.GetPosition(cards.Count, i).Y - height * 0.5f * HandPosHelper.GetScale(cards.Count).Y;
                    if (hand.ActiveHolders.Count > 0)
                    {
                        handTop = Mathf.Min(handTop, hand.ActiveHolders.Min(h => h.Position.Y - h.Hitbox.Size.Y * 0.5f * h.Scale.Y));
                    }
                    handTop -= 20f; // clear the cost badges, which stick out above your cards
                    _focusBottom ??= Mathf.Max(handTop, (layer.GetGlobalTransformWithCanvas().AffineInverse() * LastMouse).Y + 4f);
                    position.Y = _focusBottom.Value - height * 0.5f;
                }
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
        // The closeup (and a dragged card) use the lighter "when hand hidden" shadow; the rest keep the layer's.
        float lifted = (1f - 0.6f * Settings.HandShadowWhenHidden) / shade;
        foreach (var (model, holder) in held)
        {
            if (!GodotObject.IsInstanceValid(holder)) continue;
            Color target = holder == closeup || holder == _drag ? new Color(lifted, lifted, lifted) : Colors.White;
            if (holder.Modulate != target) holder.Modulate = target;
            if (!refresh || holder.CardNode is not NCard node) continue;
            if (holder != _drag) node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal); // keep the drag's preview
            ApplyGlow(holder, node, TeammateGlow(model, viewed));
        }
    }

    /// <summary>Pressing a teammate's card shows its closeup at once (skips the hover delay) and may start a drag.</summary>
    private static void OnTeammateCardPressed(NHandCardHolder holder)
    {
        if (_inputBlocked || _drag != null) return;
        _clicked = holder;
        _pressed = holder;
        _pressMouse = LastMouse;
    }

    /// <summary>
    /// The hovered card's closeup is due: right away when clicked, otherwise after the hover delay (never when the
    /// delay is at its maximum, "disabled").
    /// </summary>
    private static NHandCardHolder? CloseupHolder(double now)
    {
        if (_focused == null)
        {
            if (_hoverActive && now - _lastUnfocus > HoverSessionGrace) _hoverActive = false;
            return null;
        }
        if (_inputBlocked) return null;
        // The no-delay hover between neighbours stays within one teammate's hand: moving onto a card of another
        // shown hand starts the delay again (its focus time is when the mouse got there).
        ulong? owner = OwnerOf(_focused);
        if (_hoverActive && owner != _sessionOwner) _hoverActive = false;
        _sessionOwner = owner;
        bool disabled = Settings.HoverDelay >= Settings.MaxHoverDelay;
        if (_clicked == _focused || _drag == _focused)
        {
            _hoverActive = !disabled; // with hover disabled, only the clicked card shows
            return _focused;
        }
        if (disabled) return null;
        if (_hoverActive) return _focused;
        if (now - _focusStart < Settings.HoverDelay) return null;
        _hoverActive = true;
        return _focused;
    }

    /// <summary>
    /// The native hover raises the card above yours and shows its tooltips immediately; until the closeup is due,
    /// undo both, then bring them back.
    /// </summary>
    private static void HoldBackHoverEffects(NHandCardHolder? closeup)
    {
        if (_focused != null && closeup == null && _focused != _drag)
        {
            if (_focused.ZIndex != 0) _focused.ZIndex = 0;
            NHoverTipSet.Remove(_focused);
            _tipsHeld = true;
        }
        else if (closeup != null && _tipsHeld && closeup != _drag)
        {
            _tipsHeld = false;
            closeup.ZIndex = 1;
            if (closeup.CardNode?.Model is CardModel model) NHoverTipSet.CreateAndShow(closeup, model.HoverTips)?.SetAlignmentForCardHolder(closeup);
        }
    }

    private static NTargetManager? TargetManager => NRun.Instance?.GlobalUi?.TargetManager;

    /// <summary>While you're playing one of your own cards, teammate cards ignore the mouse (no hover, no drag).</summary>
    private static void BlockWhileYouPlay(NPlayerHand hand)
    {
        bool block = _drag == null && (hand.InCardPlay || TargetManager?.IsInSelection == true);
        if (block == _inputBlocked) return;
        _inputBlocked = block;
        foreach (NHandCardHolder holder in AllHolders)
        {
            holder.Hitbox.MouseFilter = block ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        }
        if (block && _focused != null)
        {
            _focused.ZIndex = 0;
            NHoverTipSet.Remove(_focused);
            _focused = null;
        }
        _clicked = null;
        _pressed = null;
        ModLog.Info($"HandViewer: teammate cards {(block ? "ignore the mouse while you play a card" : "respond to the mouse again")}");
    }

    /// <summary>Viewport point in the dragged card's layer (every hand's layer shares your hand container's transform).</summary>
    private static Vector2 ToLayer(Vector2 viewportPos) => ((Control)_drag!.GetParent()).GetGlobalTransformWithCanvas().AffineInverse() * viewportPos;

    private static float _dragStartY;

    /// <summary>
    /// Preview-only drag, following the game's own card play (NMouseCardPlay) but never playing anything: a card
    /// dragged into the play zone aims with the game's targeting arrow (single target) or shows its all-enemies
    /// numbers; the card shows the numbers for whatever it points at, from the teammate's state (their Strength,
    /// their hits this turn, ...). Letting go over a target, or in the play zone, gets the "own hand" line.
    /// </summary>
    private static void UpdateDrag()
    {
        bool leftDown = Input.IsMouseButtonPressed(MouseButton.Left);
        if (_pressed != null && _drag == null)
        {
            if (!GodotObject.IsInstanceValid(_pressed) || !leftDown) _pressed = null;
            else if ((LastMouse - _pressMouse).Length() > CardDragThreshold) StartDrag(_pressed);
        }
        if (_drag == null) return;
        if (!GodotObject.IsInstanceValid(_drag) || _drag.CardNode?.Model is not CardModel card)
        {
            EndDrag(attempted: false);
            return;
        }
        _drag.ZIndex = 2;
        if (_targeting) return; // the game's targeting runs until you pick a target or cancel

        Vector2 size = _drag.GetViewportRect().Size;
        float playZone = size.Y * 0.75f;
        if (_dragStartY > playZone) playZone = Mathf.Max(playZone, _dragStartY - 100f);
        bool inPlayZone = LastMouse.Y < playZone;
        if (Input.IsMouseButtonPressed(MouseButton.Right) || LastMouse.Y > size.Y * 0.95f)
        {
            EndDrag(attempted: false);
            return;
        }
        if (card.TargetType is TargetType.AnyEnemy or TargetType.AnyAlly)
        {
            if (inPlayZone)
            {
                BeginTargeting(card, size);
                return;
            }
            if (!leftDown)
            {
                EndDrag(attempted: false);
                return;
            }
        }
        else
        {
            if (inPlayZone != _multiShown) ShowMultiTargeting(card, inPlayZone);
            if (!leftDown)
            {
                EndDrag(attempted: inPlayZone);
                return;
            }
        }
        _drag.SetTargetPosition(ToLayer(LastMouse));
    }

    private static void StartDrag(NHandCardHolder holder)
    {
        _drag = holder;
        _pressed = null;
        _dragStartY = _pressMouse.Y;
        NHoverTipSet.Remove(holder);
        holder.BeginDrag();
        holder.Hitbox.MouseFilter = Control.MouseFilterEnum.Ignore; // like the game: don't block what's under the card
        // Like dragging your own card: the game shows teammates the card over your head (its own sync message, so
        // players without the mod see it too).
        if (Settings.SendsCardSignals && holder.CardNode?.Model is CardModel model) RunManager.Instance?.HoveredModelTracker?.OnLocalCardSelected(model);
        ModLog.Info($"HandViewer: preview drag {holder.CardNode?.Model?.Id.Entry}");
    }

    private static void BeginTargeting(CardModel card, Vector2 size)
    {
        NTargetManager? manager = TargetManager;
        if (manager == null || _drag?.CardNode is not NCard node) return;
        _targeting = true;
        _drag.SetTargetPosition(ToLayer(new Vector2(size.X / 2f, size.Y - _drag.Hitbox.Size.Y * 0.75f / 2f)));
        _drag.SetTargetScale(Vector2.One * 0.75f);
        manager.CreatureHovered += OnCreatureHovered;
        manager.CreatureUnhovered += OnCreatureUnhovered;
        // The game judges "any ally" from your side (anyone but you); for a teammate's card it's anyone but them —
        // so you're a valid target, the card's owner isn't.
        Player owner = card.Owner;
        bool ally = card.TargetType == TargetType.AnyAlly;
        Func<Node, bool>? notOwner = ally
            ? n => n is NCreature c ? c.Entity?.Player != owner : n is not NMultiplayerPlayerState s || s.Player != owner
            : null;
        manager.StartTargeting(ally ? TargetType.AnyPlayer : card.TargetType, node, TargetMode.ReleaseMouseToTarget,
            () => _drag == null || LastMouse.Y > size.Y * 0.95f, notOwner);
        ModLog.Info($"HandViewer: preview aiming {card.Id.Entry}");
        TaskHelper.RunSafely(AwaitTarget(manager));
    }

    private static async Task AwaitTarget(NTargetManager manager)
    {
        Node? picked = await manager.SelectionFinished();
        manager.CreatureHovered -= OnCreatureHovered;
        manager.CreatureUnhovered -= OnCreatureUnhovered;
        if (!_targeting) return; // already ended
        _targeting = false;
        EndDrag(attempted: picked != null);
    }

    private static void OnCreatureHovered(NCreature creature)
    {
        _drag?.CardNode?.SetPreviewTarget(creature.Entity);
        ModLog.Info($"HandViewer: preview target {creature.Entity?.Name} for {_drag?.CardNode?.Model?.Id.Entry}");
    }

    private static void OnCreatureUnhovered(NCreature _) => _drag?.CardNode?.SetPreviewTarget(null);

    /// <summary>All-enemies (and self) cards in the play zone: the game's multi-target numbers and reticles.</summary>
    private static void ShowMultiTargeting(CardModel card, bool show)
    {
        _multiShown = show;
        if (_drag?.CardNode is not NCard node || NCombatRoom.Instance is not NCombatRoom room) return;
        IReadOnlyList<Creature> enemies = card.TargetType is TargetType.AllEnemies or TargetType.RandomEnemy
            ? card.CombatState?.HittableEnemies ?? (IReadOnlyList<Creature>)Array.Empty<Creature>()
            : Array.Empty<Creature>();
        var marked = enemies.ToList();
        if (card.TargetType == TargetType.Self) marked.Add(card.Owner.Creature);
        foreach (Creature creature in marked)
        {
            if (room.GetCreatureNode(creature) is NCreature c)
            {
                if (show) c.ShowMultiselectReticle();
                else c.HideMultiselectReticle();
            }
        }
        node.SetPreviewTarget(show && enemies.Count == 1 ? enemies[0] : null);
        node.UpdateVisuals(PileType.Hand, show ? CardPreviewMode.MultiCreatureTargeting : CardPreviewMode.Normal);
        ModLog.Info($"HandViewer: preview all targets {(show ? "on" : "off")} for {card.Id.Entry}");
    }

    private static void EndDrag(bool attempted)
    {
        NHandCardHolder? holder = _drag;
        if (holder == null) return;
        if (_targeting)
        {
            _targeting = false;
            TargetManager?.CancelTargeting();
        }
        if (_multiShown && holder.CardNode?.Model is CardModel card) ShowMultiTargeting(card, false);
        _multiShown = false;
        _drag = null;
        RunManager.Instance?.HoveredModelTracker?.OnLocalCardDeselected();
        _clicked = null;
        if (GodotObject.IsInstanceValid(holder))
        {
            holder.CardNode?.SetPreviewTarget(null);
            holder.CardNode?.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            holder.ZIndex = 0;
            holder.Hitbox.MouseFilter = _inputBlocked ? Control.MouseFilterEnum.Ignore : Control.MouseFilterEnum.Stop;
        }
        ModLog.Info($"HandViewer: preview drag ended (attempted play: {attempted})");
        if (!attempted) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now < _nextBubble) return;
        _nextBubble = now + 2.0;
        PileViewer.Say("I can only play cards from my own hand.");
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

    private static void ClearCards()
    {
        SyncHover(null);
        EndDrag(attempted: false);
        _pressed = null;
        _clicked = null;
        _focused = null;
        for (int i = _hands.Count - 1; i >= 0; i--) RemoveHand(i);
        _glow.Clear();
    }

    // ---- hv11: a teammate trying out one of your cards ----

    /// <summary>Card over a teammate's head (the game's multiplayer intent) that is glowing because it's yours, by teammate.</summary>
    private static readonly Dictionary<ulong, (NCard Card, Tween Pulse)> _signals = new();

    private const float SignalGlowWidth = 0.1f;

    /// <summary>
    /// The game shows the card a teammate hovers or drags over their head. When it's one of *your* cards (they're
    /// trying it out from their Hand Viewer — a hint to play it), it pulses with a gold glow. Called after the game
    /// updates that intent.
    /// </summary>
    internal static void OnTeammateIntentChanged(NMultiplayerPlayerIntentHandler handler)
    {
        var t = Traverse.Create(handler);
        if (t.Field("_player").GetValue() is not Player teammate) return;
        bool mine = Settings.ReceivesCardSignals && t.Field("_displayedModel").GetValue() is CardModel card && card.Owner?.NetId == LocalNetId
            && teammate.NetId != LocalNetId;
        NCard? shownNode = handler.CardIntent.GetNodeOrNull<NCard>("%Card");
        if (mine && _signals.TryGetValue(teammate.NetId, out var current) && current.Card == shownNode && current.Pulse.IsValid()) return;
        if (_signals.Remove(teammate.NetId, out var old))
        {
            old.Pulse.Kill();
            if (GodotObject.IsInstanceValid(old.Card)) old.Card.CardHighlight.AnimHide();
        }
        if (!mine || shownNode is not NCard shown) return;
        // Blink about once a second: the glow's width (what NCardHighlight.AnimShow animates to 0.075) from none
        // to a little wider than usual and back, so it catches the eye.
        NCardHighlight glow = shown.CardHighlight;
        glow.Modulate = NCardHighlight.gold;
        if (glow.Material is not ShaderMaterial material) return;
        void SetWidth(float w) => material.SetShaderParameter("width", w);
        Tween pulse = shown.CreateTween().SetLoops();
        pulse.TweenMethod(Callable.From<float>(SetWidth), 0f, SignalGlowWidth, 0.5).SetTrans(Tween.TransitionType.Sine);
        pulse.TweenMethod(Callable.From<float>(SetWidth), SignalGlowWidth, 0f, 0.5).SetTrans(Tween.TransitionType.Sine);
        _signals[teammate.NetId] = (shown, pulse);
        ModLog.Info($"HandViewer: {teammate.NetId} is trying out your {((CardModel)t.Field("_displayedModel").GetValue()).Id.Entry}");
    }

    private static void StopSignals()
    {
        foreach (var (card, pulse) in _signals.Values)
        {
            pulse.Kill();
            if (GodotObject.IsInstanceValid(card)) card.CardHighlight.AnimHide();
        }
        _signals.Clear();
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

}
