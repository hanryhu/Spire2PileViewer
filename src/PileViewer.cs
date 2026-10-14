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
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
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
public static partial class PileViewer
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
    // Float alignment: the default spot above the character (top-centre of the Pile Viewer).
    private static readonly Vector2 FloatAnchor = new(0f, -350f);

    // Gap between a player's name block in the top-left player list and their Pile Viewer.
    private const float RowGap = 14f;

    /// <summary>Strip scale at the "Size 100%" setting: about as tall as a player's name + health bar.</summary>
    internal const float BaseScale = 0.75f;

    private static float StripScale => Settings.Scale * BaseScale;

    /// <summary>Rows whose native hand-count icon we hid (our strip shows the hand count instead).</summary>
    private static readonly HashSet<NMultiplayerPlayerState> _handCountHidden = new();

    /// <summary>Player whose row is lit because the mouse is over their strip (like hovering their character).</summary>
    private static Player? _highlighted;

    // Launch with -pileviewerself to also show a strip for the local player (handy for solo testing).
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

    /// <summary>
    /// Mouse over a strip lights up that player's frame in the player list (NMultiplayerPlayerStateContainer
    /// .HighlightPlayer — what hovering their character does), so it's clear whose piles these are.
    /// </summary>
    private static void UpdateHoverHighlight()
    {
        Vector2 mouse = HandViewer.LastMouse;
        Player? hovered = _widgets.Values.FirstOrDefault(w => GodotObject.IsInstanceValid(w.Root) && w.Root.IsVisibleInTree()
            && ScreenRect(w.Root).HasPoint(mouse))?.Player;
        SetHighlight(hovered);
    }

    private static void SetHighlight(Player? player)
    {
        if (player == _highlighted) return;
        var container = NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if (_highlighted != null) container?.UnhighlightPlayer(_highlighted);
        _highlighted = player;
        if (player != null) container?.HighlightPlayer(player);
    }

    /// <summary>
    /// Hide the row's native hand-count icon while our strip shows that player's hand size. The game shows it at the
    /// start of combat and hides it at the end, so it's only put back while combat is still running.
    /// </summary>
    private static void HideNativeHandCount(NMultiplayerPlayerState row, bool hide)
    {
        if (row.GetNodeOrNull<Control>("%CardCountContainer") is not Control count) return;
        if (hide)
        {
            if (count.Visible) count.Visible = false;
            _handCountHidden.Add(row);
        }
        else if (_handCountHidden.Remove(row) && CombatManager.Instance.IsInProgress)
        {
            count.Visible = true;
        }
    }

    private static void RestoreNativeHandCounts()
    {
        foreach (var row in _handCountHidden.ToList())
        {
            if (GodotObject.IsInstanceValid(row)) HideNativeHandCount(row, false);
        }
        _handCountHidden.Clear();
    }

    public static void Hide()
    {
        SetHighlight(null);
        RestoreNativeHandCounts();
        foreach (var widget in _widgets.Values) widget.Free();
        _widgets.Clear();
        HandViewer.Reset();
    }

    /// <summary>Back at the main menu: also forget whose hand was shown this run.</summary>
    public static void OnReturnToMainMenu()
    {
        Hide();
        HandViewer.ResetRun();
    }

    /// <summary>How many thought bubbles the mod has shown (auto-test).</summary>
    internal static int SaidCount;

    /// <summary>A thought bubble over your own character (like the game's "empty pile" messages).</summary>
    internal static void Say(string message)
    {
        CombatState? state = CombatManager.Instance.DebugOnlyGetState();
        if (state == null || LocalContext.GetMe(state) is not Player me) return;
        ModLog.Info($"PileViewer: says '{message}'");
        SaidCount++;
        NCombatRoom.Instance?.CombatVfxContainer.AddChildSafely(NThoughtBubbleVfx.Create(message, me.Creature, 2.0));
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
        // The Hand Viewer works without the strips (default hand, hotkeys, Hide hand button).
        HandViewer.Update(room, id => state.Players.FirstOrDefault(p => p != null && p.NetId == id && p.NetId != localNetId));
        if (!Settings.Enabled)
        {
            foreach (var widget in _widgets.Values) widget.Free();
            _widgets.Clear();
            SetHighlight(null);
            RestoreNativeHandCounts();
            return;
        }
        bool includeSelf = Settings.ShowSelf || IncludeSelf;
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
        UpdateHoverHighlight();
    }

    /// <summary>The player's row in the top-left player list (name, health, ...), which their strip sits next to.</summary>
    internal static NMultiplayerPlayerState? PlayerRow(Player player) =>
        NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer?.GetChildren().OfType<NMultiplayerPlayerState>()
            .FirstOrDefault(row => row.Player == player);

    /// <summary>
    /// On-screen rect of a player's name block: from the row's left edge to whichever ends further right — the
    /// visible name line (name, energy, ...) or the health bar, which the game widens with max HP (175 units at 80
    /// max HP) — plus the game's 6-unit margin. Same rule the game uses to size the row's gold frame, computed
    /// every frame (the game only re-measures it while the row is highlighted).
    /// </summary>
    internal static Rect2 NameBlockRect(NMultiplayerPlayerState row)
    {
        Rect2 rowRect = ScreenRect(row.Hitbox);
        float right = rowRect.End.X;
        if (row.GetNodeOrNull<Control>("TopInfoContainer") is Control top)
        {
            foreach (Control item in top.GetChildren().OfType<Control>())
            {
                if (item.Visible && item.Size.X > 0f) right = Mathf.Max(right, ScreenRect(item).End.X + 6f);
            }
        }
        if (row.GetNodeOrNull<NHealthBar>("%HealthBar")?.HpBarContainer is Control bar) right = Mathf.Max(right, ScreenRect(bar).End.X + 6f);
        return new Rect2(rowRect.Position, new Vector2(right - rowRect.Position.X, rowRect.Size.Y));
    }

    /// <summary>
    /// Default left edge for Fixed Pile Viewers (screen x): halfway through the 4th relic slot of your relic bar,
    /// measured from its first slot and spacing (so it doesn't depend on how many relics you have), but not left of a
    /// starting character's name block plus padding.
    /// </summary>
    internal static float FixedOriginCap()
    {
        float cap = 0f;
        var relics = NRun.Instance?.GlobalUi?.RelicInventory;
        if (relics != null && relics.RelicNodes.Count > 0)
        {
            Rect2 first = ScreenRect(relics.RelicNodes[0]);
            float step = first.Size.X + relics.GetThemeConstant("h_separation") * relics.GetGlobalTransformWithCanvas().Scale.X;
            cap = first.Position.X + 3.5f * step;
        }
        // Never left of where a starting character's name block ends (health bar at 80 max HP, the most any
        // character starts with, + the game's 6-unit margin) plus padding — otherwise starting rows wouldn't line up.
        if (PlayerRowAny() is { } row && row.GetNodeOrNull<NHealthBar>("%HealthBar")?.HpBarContainer is Control bar)
        {
            Rect2 barRect = ScreenRect(bar);
            float scale = row.GetGlobalTransformWithCanvas().Scale.X;
            cap = Mathf.Max(cap, barRect.Position.X + (StartingBarWidth + 6f + RowGap) * scale);
        }
        return cap;
    }

    // Width the game gives a health bar at 80 max HP (UpdateWidthRelativeToReferenceValue(80, 175)).
    private const float StartingBarWidth = 175f;

    private static NMultiplayerPlayerState? PlayerRowAny() =>
        NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer?.GetChildren().OfType<NMultiplayerPlayerState>().FirstOrDefault();

    /// <summary>A control's rect on screen (it and the player list can be on different canvas layers).</summary>
    internal static Rect2 ScreenRect(Control control)
    {
        Transform2D xf = control.GetGlobalTransformWithCanvas();
        return new Rect2(xf.Origin, control.Size * xf.Scale);
    }

    private static void UpdateWidget(NCombatRoom room, Widget widget)
    {
        NMultiplayerPlayerState? row = PlayerRow(widget.Player);
        bool floating = Settings.Alignment == Settings.PileViewerAlignment.Float;
        NCreature? creature = floating ? room.GetCreatureNode(widget.Player.Creature) : null;
        // Hide under full-screen overlays (pile screens, deck view, map, ...) — the strip would draw on top of them.
        bool overlayOpen = NCapstoneContainer.Instance?.InUse == true;
        bool anchored = floating ? creature != null : row != null && row.IsVisibleInTree();
        if (!anchored || widget.Player.PlayerCombatState == null || overlayOpen)
        {
            if (row != null) HideNativeHandCount(row, false);
            widget.Root.Visible = false;
            widget.ClosePopup();
            return;
        }
        widget.Root.Visible = true;

        for (int i = 0; i < StripSlots.Length; i++)
        {
            if (StripSlots[i] == PileType.Hand && widget.Buttons[i] is Button handButton)
            {
                if (handButton.Visible != Settings.HandViewerEnabled)
                {
                    handButton.Visible = Settings.HandViewerEnabled;
                    widget.Root.ResetSize(); // containers don't shrink by themselves
                }
                // Highlight the Hand slot whose hand is being shown (on your own strip: while your hand is shown).
                bool viewing = widget.Player.NetId == (RunManager.Instance?.NetService?.NetId ?? 0)
                    ? !HandViewer.HideOwnHand
                    : HandViewer.ViewedNetId == widget.Player.NetId;
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

        // The strip's Hand slot shows the hand size, so the row's own hand-count icon is redundant.
        if (row != null) HideNativeHandCount(row, Settings.HandViewerEnabled);

        // Settings from the F1 menu: size and background opacity.
        if (widget.Root.Scale.X != StripScale) widget.Root.Scale = new Vector2(StripScale, StripScale);
        if (widget.Style.BgColor.A != Settings.Opacity)
        {
            widget.Style.BgColor = new Color(StripBg, Settings.Opacity);
            widget.Style.BorderColor = new Color(StripBorder, Settings.Opacity);
        }

        Vector2 size = widget.Root.Size * StripScale;
        if (floating)
        {
            // Float: centred above the character (moves with it), plus the player's drag offset.
            widget.Root.GlobalPosition = creature!.GlobalPosition + FloatAnchor + Settings.GetOffset(widget.Player.NetId)
                - new Vector2(size.X / 2f, 0f);
        }
        else
        {
            // Fixed: left edge at the cap (halfway through the 4th relic, so short names still give a left-aligned
            // column) or right after the player's name block if that's further right (long names, health bars that
            // grow with max HP). A drag offset moves the cap; the name block still pushes. Anchored at the centre of
            // its left edge, so resizing keeps that point in place.
            Rect2 block = NameBlockRect(row!);
            Vector2 offset = Settings.GetOffset(widget.Player.NetId);
            Vector2 onScreen = new Vector2(Mathf.Max(FixedOriginCap() + offset.X, block.End.X + RowGap),
                block.GetCenter().Y - size.Y / 2f + offset.Y);
            widget.Root.GlobalPosition = widget.Root.GetCanvasTransform().AffineInverse() * onScreen;
        }

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
            TooltipText = PileName(pileType),
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

    private const string DragHint = "Drag to move";

    // A press that moves more than this is a drag (moves the strip), not a click on a slot.
    private const float DragThreshold = 6f;

    private static bool _pressed;

    private static bool _dragging;

    private static ulong _dragNetId;

    private static Vector2 _dragStartMouse;

    private static Vector2 _dragStartOffset;

    /// <summary>
    /// Left-drag anywhere on a strip moves just that strip (its own offset, saved on release; reset from the F1
    /// menu). A press that doesn't move past the threshold stays a click on the slot.
    /// </summary>
    internal static void HandleDragInput(ulong netId, Control control, InputEvent ev)
    {
        if (ev is InputEventMouseButton { ButtonIndex: MouseButton.Left } mb)
        {
            if (mb.Pressed)
            {
                _pressed = true;
                _dragging = false;
                _dragNetId = netId;
                _dragStartMouse = mb.GlobalPosition;
                _dragStartOffset = Settings.GetOffset(netId);
            }
            else
            {
                _pressed = false;
                if (_dragging)
                {
                    _dragging = false;
                    Settings.Save();
                    ModLog.Info($"PileViewer: strip offset saved for player {_dragNetId}: {Settings.GetOffset(_dragNetId)}");
                    control.AcceptEvent(); // a drag is not a click
                }
            }
        }
        else if (ev is InputEventMouseMotion mm && _pressed)
        {
            Vector2 delta = mm.GlobalPosition - _dragStartMouse;
            if (_dragging || delta.Length() > DragThreshold)
            {
                _dragging = true;
                Settings.SetOffset(_dragNetId, _dragStartOffset + delta);
                control.AcceptEvent();
            }
        }
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

    internal readonly record struct BadgeStyle(Texture2D? Texture, Vector2 Position, Vector2 Size, Color Tint,
        Color TextColor, Color OutlineColor, int OutlineSize, int FontSize);

    /// <summary>
    /// Placement and look of the native button's count (CountContainer, its optional Background badge, and the
    /// Count label's colors/outline/size), mapped from the native button onto a slot. Draw/discard use a red
    /// badge in a corner; exhaust has no badge and centers a purple-outlined number on the icon.
    /// Falls back to a bottom-right badge.
    /// </summary>
    // Badges with a background circle (draw, hand, discard) are drawn this much bigger than the game's, about their
    // centre, but the visible circle never wider than half the slot's icon.
    private const float BadgeGrowth = 1.25f;

    private static readonly Dictionary<Texture2D, float> _visibleFraction = new();

    /// <summary>Visible circle diameter of a badge: its texture has a transparent margin around the circle.</summary>
    internal static float BadgeCircle(BadgeStyle style)
    {
        if (style.Texture is not Texture2D tex) return style.Size.X;
        if (!_visibleFraction.TryGetValue(tex, out float fraction))
        {
            // Width of the solid circle across the middle row (alpha >= 0.9), ignoring the soft shadow around it.
            fraction = 1f;
            if (tex.GetImage() is Image image && image.GetWidth() > 0)
            {
                if (image.IsCompressed()) image.Decompress();
                int y = image.GetHeight() / 2, first = -1, last = -1;
                for (int x = 0; x < image.GetWidth(); x++)
                {
                    if (image.GetPixel(x, y).A < 0.9f) continue;
                    if (first < 0) first = x;
                    last = x;
                }
                if (first >= 0) fraction = (last - first + 1) / (float)image.GetWidth();
            }
            _visibleFraction[tex] = fraction;
        }
        return style.Size.X * fraction;
    }

    private static BadgeStyle Enlarged(BadgeStyle style)
    {
        if (style.Texture == null) return style; // exhaust: a plain number over the icon
        float circle = BadgeCircle(style);
        float grow = Mathf.Min(BadgeGrowth, SlotSize / 2f / circle);
        if (grow <= 1f) return style;
        Vector2 size = style.Size * grow;
        Vector2 position = style.Position - (size - style.Size) / 2f;
        return style with { Position = position, Size = size, FontSize = (int)Math.Round(style.FontSize * grow),
            OutlineSize = Math.Max(1, (int)Math.Round(style.OutlineSize * grow)) };
    }

    private static BadgeStyle GetBadgeStyle(NCombatRoom room, PileType pileType, bool enlarge = true)
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
            if (enlarge) style = Enlarged(style);
            if (enlarge && _loggedBadges.Add(pileType)) ModLog.Info($"PileViewer: {pileType} badge {style} (k={k}, circle {BadgeCircle(style):0.#})");
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
