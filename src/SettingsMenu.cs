using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace Spire2PileViewer;

/// <summary>
/// F1 settings panel. BetterSpire2 also opens its menu on F1 (centered), so this one docks to the
/// top-right to sit beside it. Every change is saved immediately — except hotkeys, which are only
/// saved once they have no conflicts.
/// </summary>
public static class SettingsMenu
{
    public const Key Hotkey = Key.F1;

    private const float PanelWidth = 360f;

    private static readonly Color ConflictColor = new(1f, 0.35f, 0.3f);

    private static readonly Color WarningColor = new(1f, 0.8f, 0.3f);

    private static CanvasLayer? _layer;

    // Hotkeys section state.
    private static int _selectedTeammate;
    private static int _editableTeammates = Hotkeys.MaxTeammates;
    private static int? _capturingSlot;
    private static readonly Button?[] _hotkeyButtons = new Button?[Hotkeys.Slots.Length];
    private static Button? _hideHandHotkeyButton;
    private static Label? _hideHandHotkeyStatus;
    private const int HideHandCapture = -1; // _capturingSlot value while capturing the Hide/show my hand binding
    private static Label? _hotkeyStatus;

    // Exposed for the auto-test, which drives the menu through its real controls.
    internal static CheckButton? EnabledToggle;
    internal static HSlider? ScaleSlider;
    internal static HSlider? OpacitySlider;
    internal static CheckButton? ShowSelfToggle;
    internal static CheckButton? TextListToggle;
    internal static Button? ResetPositionButton;
    internal static Button? ResetAllButton;
    internal static Button? ResetHideButtonPositionButton;
    internal static Button? ResetHandViewerButton;
    internal static TabContainer? Tabs;
    internal static CheckButton? HandViewerToggle;
    internal static CheckButton? HideHandButtonToggle;
    internal static HSlider? HandShadowSlider;
    internal static HSlider? HandVisibleSlider;
    internal static HSlider? HandShadowHiddenSlider;
    internal static HSlider? HoverDelaySlider;

    internal static Button[]? MaxHandViewersButtons;

    internal static Button[]? CardSignalButtons;
    internal static Button[]? AlignmentButtons;
    private static int _selectedTab;
    internal static OptionButton? TeammatePicker;
    internal static Button? GetHotkeyButton(int slot) => _hotkeyButtons[slot];
    internal static Button? GetHotkeyButton(PileType slot) => _hotkeyButtons[Array.IndexOf(Hotkeys.Slots, slot)];
    internal static Button? HideHandHotkeyButton => _hideHandHotkeyButton;
    internal static string HotkeyStatus => _hotkeyStatus?.Text ?? "";

    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

    public static bool IsCapturing => IsOpen && _capturingSlot.HasValue;

    /// <summary>
    /// Prefix on NGame._Input: key capture while rebinding (consumed so the game doesn't also act on the
    /// key) and F1. The pile hotkeys are NOT handled here — see <see cref="HotkeyDispatchPatch"/>.
    /// </summary>
    [HarmonyPatch(typeof(NGame), "_Input")]
    public static class InputPatch
    {
        private static bool Prefix(NGame __instance, InputEvent inputEvent)
        {
            try
            {
                if (inputEvent is InputEventMouse mouse) HandViewer.LastMouse = mouse.Position;
                if (HandleKey(inputEvent))
                {
                    __instance.GetViewport()?.SetInputAsHandled();
                    return false;
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("SettingsMenu.InputPatch", ex);
            }
            return true;
        }
    }

    /// <summary>Returns true if the event was consumed. Public to the auto-test so it can feed real key events.</summary>
    internal static bool HandleKey(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Pressed: true } mouse && KeyCombo.IsBindable(mouse.ButtonIndex))
        {
            if (IsCapturing)
            {
                SetCaptured(KeyCombo.From(mouse));
                return true;
            }
            return DispatchHotkey(mouse);
        }
        if (inputEvent is not InputEventKey { Pressed: true } key || key.IsEcho()) return false;

        if (IsCapturing)
        {
            if (IsModifier(key.Keycode)) return true; // wait for the actual key
            int slot = _capturingSlot!.Value;
            _capturingSlot = null;
            if (key.Keycode == Key.Escape)
            {
                ModLog.Info("SettingsMenu: capture cancelled");
            }
            else
            {
                _capturingSlot = slot;
                SetCaptured(key.Keycode is Key.Backspace or Key.Delete ? null : KeyCombo.From(key));
                return true;
            }
            RefreshHotkeys();
            return true;
        }

        if (key.Keycode == Hotkey && !key.ShiftPressed && !key.CtrlPressed && !key.AltPressed && !IsTyping())
        {
            Toggle();
        }
        return false; // F1 is not consumed, so BetterSpire2's F1 menu still opens too
    }

    private static void SetCaptured(KeyCombo? combo)
    {
        int slot = _capturingSlot!.Value;
        _capturingSlot = null;
        (int row, int col) = slot == HideHandCapture ? (Hotkeys.GlobalRow, 0) : (_selectedTeammate, slot);
        Hotkeys.Draft[row, col] = combo;
        ModLog.Info($"SettingsMenu: {Hotkeys.Describe(row, col)} = {combo?.ToString() ?? "unbound"}");
        Hotkeys.TryCommitDraft(_editableTeammates);
        RefreshHotkeys();
    }

    /// <summary>Runs the hotkey bound to this key or mouse button, if any. Returns whether something happened.</summary>
    private static bool DispatchHotkey(InputEvent ev)
    {
        if (IsTyping() || IsCapturing) return false;
        bool hide = Hotkeys.MatchHideHand(ev);
        var hit = hide ? null : Hotkeys.Match(ev);
        if (!hide && hit == null) return false;
        return hide ? HandViewer.ToggleHideOwnHandHotkey()
            : hit!.Value.pile == PileType.Hand ? HandViewer.ToggleTeammate(hit.Value.teammate)
            : PileViewer.OpenTeammatePile(hit.Value.teammate, hit.Value.pile);
    }

    /// <summary>
    /// The pile hotkeys run as a postfix on the game's own hotkey dispatcher (NHotkeyManager._UnhandledInput),
    /// so they rank below the game: they only fire for keys nothing else handled, never while the dev
    /// console is open or a text field is being edited, and never for a key the game uses itself.
    /// </summary>
    [HarmonyPatch(typeof(NHotkeyManager), "_UnhandledInput")]
    public static class HotkeyDispatchPatch
    {
        private static void Postfix(NHotkeyManager __instance, InputEvent inputEvent)
        {
            try
            {
                if (inputEvent is not InputEventKey { Pressed: true } key || key.IsEcho()) return;
                Viewport? viewport = __instance.GetViewport();
                if (viewport == null || viewport.IsInputHandled() || IsTyping() || IsCapturing) return;
                if (!Hotkeys.MatchHideHand(key) && Hotkeys.Match(key) == null) return;
                if (Hotkeys.NativeAction(key.Keycode) is string action)
                {
                    ModLog.Info($"Hotkey {KeyCombo.From(key)} skipped: the game's '{action}' uses {key.Keycode}");
                    return;
                }
                if (DispatchHotkey(key)) viewport.SetInputAsHandled();
            }
            catch (Exception ex)
            {
                ModLog.Error("SettingsMenu.HotkeyDispatchPatch", ex);
            }
        }
    }

    /// <summary>Dev console open, or a text field being edited (same checks the game's hotkey manager uses).</summary>
    private static bool IsTyping()
    {
        if (NDevConsole.IsConsoleVisible) return true;
        Control? focus = NGame.Instance?.GetViewport()?.GuiGetFocusOwner();
        return focus is LineEdit { Editable: true } || focus is TextEdit { Editable: true };
    }

    private static bool IsModifier(Key key) =>
        key is Key.Shift or Key.Ctrl or Key.Alt or Key.Meta or Key.Capslock;

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public static void Close()
    {
        if (IsOpen) _layer!.QueueFree();
        _layer = null;
        _capturingSlot = null;
        // Unsaved (conflicting) hotkey edits are dropped when the menu closes.
        Hotkeys.Draft = (KeyCombo?[,])Hotkeys.Active.Clone();
    }

    public static void Open()
    {
        if (IsOpen) _layer!.QueueFree();
        _layer = null;
        _capturingSlot = null;
        NGame? game = NGame.Instance;
        if (game == null) return;

        _layer = new CanvasLayer { Layer = 101 };
        game.AddChild(_layer);

        var panel = new PanelContainer { CustomMinimumSize = new Vector2(PanelWidth, 0f) };
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.08f, 0.12f, 0.96f),
            BorderColor = new Color(0.8f, 0.6f, 0.2f),
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(8);
        style.SetContentMarginAll(14);
        panel.AddThemeStyleboxOverride("panel", style);
        _layer.AddChild(panel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        panel.AddChild(box);

        var header = new HBoxContainer();
        box.AddChild(header);
        var title = new Label { Text = "Pile Viewer", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color(0.9f, 0.7f, 0.2f));
        header.AddChild(title);
        var close = new Button { Text = "X", CustomMinimumSize = new Vector2(28f, 28f), FocusMode = Control.FocusModeEnum.None };
        close.Pressed += Close;
        header.AddChild(close);

        // Tabs: one per feature.
        Tabs = new TabContainer();
        Tabs.AddThemeFontSizeOverride("font_size", 14);
        box.AddChild(Tabs);
        var pileTab = new VBoxContainer { Name = "Pile Viewer" };
        pileTab.AddThemeConstantOverride("separation", 8);
        Tabs.AddChild(pileTab);
        var handTab = new VBoxContainer { Name = "Hand Viewer" };
        handTab.AddThemeConstantOverride("separation", 8);
        Tabs.AddChild(handTab);
        Tabs.CurrentTab = _selectedTab;
        Tabs.TabChanged += tab => _selectedTab = (int)tab;
        VBoxContainer outer = box;
        box = pileTab;

        EnabledToggle = AddToggle(box, "Enable pile viewer", Settings.Enabled, v => Settings.Enabled = v);
        ScaleSlider = AddSlider(box, "Size", Settings.MinScale * 100f, Settings.MaxScale * 100f, Settings.Scale * 100f,
            v => Settings.Scale = (float)v / 100f);
        OpacitySlider = AddSlider(box, "Background opacity", 0f, 100f, Settings.Opacity * 100f,
            v => Settings.Opacity = (float)v / 100f);
        AlignmentButtons = AddChoice(box, "Pile Viewer alignment", new[] { "Fixed", "Float" }, (int)Settings.Alignment,
            i => Settings.Alignment = (Settings.PileViewerAlignment)i);
        ShowSelfToggle = AddToggle(box, "Show my own piles", Settings.ShowSelf, v => Settings.ShowSelf = v);
        TextListToggle = AddToggle(box, "Show piles as text", Settings.TextList, v => Settings.TextList = v);

        AddHotkeysSection(box);

        ResetPositionButton = AddButton(box, "Reset Pile Viewer positions", Settings.ResetStripPositions);
        ResetAllButton = AddButton(box, "Reset Pile Viewer settings", () =>
        {
            Settings.ResetPileViewer();
            Open(); // rebuild so the controls show the defaults
        });
        AddDragHint(box);

        AddHandViewerTab(handTab);
        box = outer;

        Vector2 viewport = game.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        panel.Position = new Vector2(viewport.X - PanelWidth - 40f, 90f);
        RefreshHotkeys();
        ModLog.Info("SettingsMenu: opened");
    }

    /// <summary>
    /// Teammate dropdown (only the players actually in the run, by P-number and character; generic
    /// "Teammate N" outside a run) plus one rebind button per pile, like the game's keybinding screen.
    /// </summary>
    private static void AddHotkeysSection(VBoxContainer box)
    {
        var heading = new Label { Text = "Hotkeys" };
        heading.AddThemeFontSizeOverride("font_size", 16);
        heading.AddThemeColorOverride("font_color", new Color(0.9f, 0.7f, 0.2f));
        box.AddChild(heading);

        TeammatePicker = new OptionButton { FocusMode = Control.FocusModeEnum.None };
        TeammatePicker.AddThemeFontSizeOverride("font_size", 14);
        var teammates = PileViewer.Teammates();
        _editableTeammates = teammates.Count > 0 ? Math.Min(teammates.Count, Hotkeys.MaxTeammates) : Hotkeys.MaxTeammates;
        if (teammates.Count > 0)
        {
            foreach (var (number, player) in teammates.Take(Hotkeys.MaxTeammates))
            {
                TeammatePicker.AddItem($"P{number} — {player.Character.Title.GetFormattedText()}");
            }
        }
        else
        {
            for (int t = 0; t < Hotkeys.MaxTeammates; t++) TeammatePicker.AddItem($"Teammate {t + 1}");
        }
        _selectedTeammate = Math.Clamp(_selectedTeammate, 0, TeammatePicker.ItemCount - 1);
        TeammatePicker.Selected = _selectedTeammate;
        TeammatePicker.ItemSelected += index =>
        {
            _selectedTeammate = (int)index;
            _capturingSlot = null;
            RefreshHotkeys();
        };
        box.AddChild(TeammatePicker);

        for (int slot = 0; slot < Hotkeys.Slots.Length; slot++)
        {
            var row = new HBoxContainer();
            box.AddChild(row);
            string name = PileViewer.PileName(Hotkeys.Slots[slot]);
            var label = new Label { Text = name, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            label.AddThemeFontSizeOverride("font_size", 14);
            row.AddChild(label);
            var button = new Button { CustomMinimumSize = new Vector2(150f, 0f), FocusMode = Control.FocusModeEnum.None };
            button.AddThemeFontSizeOverride("font_size", 14);
            int captured = slot;
            button.Pressed += () =>
            {
                _capturingSlot = captured;
                RefreshHotkeys();
            };
            row.AddChild(button);
            _hotkeyButtons[slot] = button;
        }

        _hotkeyStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 0f),
        };
        _hotkeyStatus.AddThemeFontSizeOverride("font_size", 12);
        box.AddChild(_hotkeyStatus);
    }

    internal static string VersionText => Version;

    private static readonly string Version = "v" + (typeof(SettingsMenu).Assembly.GetName().Version?.ToString(3) ?? "?");

    private static void AddDragHint(VBoxContainer box)
    {
        var row = new HBoxContainer();
        box.AddChild(row);
        foreach (string text in new[] { "Drag to move buttons", Version })
        {
            var label = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            if (text == Version) label.HorizontalAlignment = HorizontalAlignment.Right;
            label.AddThemeFontSizeOverride("font_size", 12);
            label.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
            row.AddChild(label);
        }
    }

    private static string Describe((int teammate, int slot) key) => Hotkeys.Describe(key.teammate, key.slot);

    private static void RefreshHotkeys()
    {
        if (!IsOpen || _hotkeyStatus == null) return;
        var conflicts = Hotkeys.DraftConflicts(_editableTeammates);
        if (_hideHandHotkeyButton != null && GodotObject.IsInstanceValid(_hideHandHotkeyButton))
        {
            bool hideIssue = conflicts.TryGetValue((Hotkeys.GlobalRow, 0), out Hotkeys.Conflict hi);
            _hideHandHotkeyButton.Text = _capturingSlot == HideHandCapture
                ? "Press a key…"
                : Hotkeys.Draft[Hotkeys.GlobalRow, 0]?.ToString() ?? "(unbound)";
            if (hideIssue) _hideHandHotkeyButton.AddThemeColorOverride("font_color", hi.Blocking ? ConflictColor : WarningColor);
            else _hideHandHotkeyButton.RemoveThemeColorOverride("font_color");
        }
        for (int slot = 0; slot < _hotkeyButtons.Length; slot++)
        {
            Button? button = _hotkeyButtons[slot];
            if (button == null || !GodotObject.IsInstanceValid(button)) continue;
            bool hasIssue = conflicts.TryGetValue((_selectedTeammate, slot), out Hotkeys.Conflict issue);
            button.Text = _capturingSlot == slot
                ? "Press a key…"
                : Hotkeys.Draft[_selectedTeammate, slot]?.ToString() ?? "(unbound)";
            if (hasIssue) button.AddThemeColorOverride("font_color", issue.Blocking ? ConflictColor : WarningColor);
            else button.RemoveThemeColorOverride("font_color");
        }

        if (_capturingSlot.HasValue)
        {
            _hotkeyStatus.Text = "Press a key or mouse button (Esc cancels, Backspace clears)";
            _hotkeyStatus.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        }
        else if (conflicts.Any(c => c.Value.Blocking))
        {
            var first = conflicts.First(c => c.Value.Blocking);
            _hotkeyStatus.Text = $"Not saved: {first.Value.Message}";
            _hotkeyStatus.AddThemeColorOverride("font_color", ConflictColor);
        }
        else if (conflicts.Count > 0)
        {
            // Prefer a warning about the teammate being viewed.
            var first = conflicts.OrderBy(c => c.Key.teammate == _selectedTeammate ? 0 : 1).First();
            _hotkeyStatus.Text = $"{Describe(first.Key)}: {first.Value.Message}";
            _hotkeyStatus.AddThemeColorOverride("font_color", WarningColor);
        }
        else
        {
            _hotkeyStatus.Text = "";
        }
        _hotkeyStatus.Visible = _hotkeyStatus.Text != "";
        if (_hideHandHotkeyStatus != null && GodotObject.IsInstanceValid(_hideHandHotkeyStatus))
        {
            _hideHandHotkeyStatus.Text = _hotkeyStatus.Text;
            _hideHandHotkeyStatus.AddThemeColorOverride("font_color", _hotkeyStatus.GetThemeColor("font_color"));
            _hideHandHotkeyStatus.Visible = _hotkeyStatus.Visible;
        }
    }

    private static void AddHandViewerTab(VBoxContainer tab)
    {
        HandViewerToggle = AddToggle(tab, "Enable hand viewer", Settings.HandViewerEnabled,
            v => Settings.HandViewerEnabled = v);
        HideHandButtonToggle = AddToggle(tab, "Show \"hide hand\" button", Settings.HideHandButtonEnabled,
            v => Settings.HideHandButtonEnabled = v);
        MaxHandViewersButtons = AddChoice(tab, "Max Hand Viewers",
            Enumerable.Range(1, Settings.MaxMaxHandViewers).Select(n => n.ToString()).ToArray(), Settings.MaxHandViewers - 1,
            i => Settings.MaxHandViewers = i + 1, buttonWidth: 44f);
        CardSignalButtons = AddChoice(tab, "Hand Viewer card signals", new[] { "Off", "Receive only", "Send + Receive" },
            (int)Settings.CardSignals, i => Settings.CardSignals = (Settings.CardSignalMode)i, stacked: true);
        HandShadowSlider = AddSlider(tab, "Teammate hand shadow", 0f, 100f, Settings.HandShadow * 100f,
            v => Settings.HandShadow = (float)v / 100f);
        HandVisibleSlider = AddSlider(tab, "% of card visible", 0f, 100f, Settings.HandVisible * 100f,
            v => Settings.HandVisible = (float)v / 100f);
        HandShadowHiddenSlider = AddSlider(tab, "Teammate hand shadow (when hand hidden)", 0f, 100f,
            Settings.HandShadowWhenHidden * 100f, v => Settings.HandShadowWhenHidden = (float)v / 100f);
        HoverDelaySlider = AddSlider(tab, "Hover delay", 0f, Settings.MaxHoverDelay, Settings.HoverDelay,
            v => Settings.HoverDelay = (float)v, step: 0.05,
            format: v => v <= 0.001 ? "instant" : v >= Settings.MaxHoverDelay - 0.001 ? "disabled" : $"{v:0.00}s");
        var hotkeyRow = new HBoxContainer();
        tab.AddChild(hotkeyRow);
        var hotkeyLabel = new Label { Text = "Hide/show hand hotkey", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hotkeyLabel.AddThemeFontSizeOverride("font_size", 14);
        hotkeyRow.AddChild(hotkeyLabel);
        _hideHandHotkeyButton = new Button { CustomMinimumSize = new Vector2(150f, 0f), FocusMode = Control.FocusModeEnum.None };
        _hideHandHotkeyButton.AddThemeFontSizeOverride("font_size", 14);
        _hideHandHotkeyButton.Pressed += () =>
        {
            _capturingSlot = HideHandCapture;
            RefreshHotkeys();
        };
        hotkeyRow.AddChild(_hideHandHotkeyButton);
        _hideHandHotkeyStatus = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 0f),
        };
        _hideHandHotkeyStatus.AddThemeFontSizeOverride("font_size", 12);
        tab.AddChild(_hideHandHotkeyStatus);

        ResetHideButtonPositionButton = AddButton(tab, "Reset \"hide hand\" position", Settings.ResetHideButtonPosition);
        ResetHandViewerButton = AddButton(tab, "Reset Hand Viewer settings", () =>
        {
            Settings.ResetHandViewer();
            Open(); // rebuild so the controls show the defaults
        });
        AddDragHint(tab);
    }

    private static CheckButton AddToggle(VBoxContainer box, string text, bool value, Action<bool> apply)
    {
        var toggle = new CheckButton { Text = text, ButtonPressed = value, FocusMode = Control.FocusModeEnum.None };
        toggle.AddThemeFontSizeOverride("font_size", 14);
        toggle.Toggled += v =>
        {
            apply(v);
            Settings.Save();
            ModLog.Info($"SettingsMenu: '{text}' = {v}");
        };
        box.AddChild(toggle);
        return toggle;
    }

    private static HSlider AddSlider(VBoxContainer box, string text, float min, float max, float value, Action<double> apply,
        double step = 5, Func<double, string>? format = null)
    {
        format ??= v => $"{v:0}%";
        var row = new HBoxContainer();
        box.AddChild(row);
        var label = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(label);
        var valueLabel = new Label { Text = format(value), CustomMinimumSize = new Vector2(64f, 0f), HorizontalAlignment = HorizontalAlignment.Right };
        valueLabel.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(valueLabel);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = step,
            Value = value,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 20f),
        };
        slider.ValueChanged += v =>
        {
            apply(v);
            valueLabel.Text = format(v);
            Settings.Save();
        };
        box.AddChild(slider);
        return slider;
    }

    /// <summary>A label with one toggle button per choice (exactly one pressed).</summary>
    /// <param name="stacked">Label on its own line, buttons below (for choices too wide to share a line with it).</param>
    private static Button[] AddChoice(VBoxContainer box, string text, string[] choices, int selected, Action<int> apply, bool stacked = false,
        float buttonWidth = 70f)
    {
        var row = new HBoxContainer();
        var label = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 14);
        if (stacked)
        {
            box.AddChild(label);
            box.AddChild(row);
        }
        else
        {
            box.AddChild(row);
            row.AddChild(label);
        }
        var group = new ButtonGroup();
        var buttons = new Button[choices.Length];
        for (int i = 0; i < choices.Length; i++)
        {
            var button = new Button
            {
                Text = choices[i], ToggleMode = true, ButtonGroup = group, ButtonPressed = i == selected,
                FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(buttonWidth, 0f),
            };
            button.AddThemeFontSizeOverride("font_size", 14);
            int choice = i;
            button.Toggled += on =>
            {
                if (!on) return;
                apply(choice);
                Settings.Save();
                ModLog.Info($"SettingsMenu: '{text}' = {choices[choice]}");
            };
            row.AddChild(button);
            buttons[i] = button;
        }
        return buttons;
    }

    private static Button AddButton(VBoxContainer box, string text, Action onPressed)
    {
        var button = new Button { Text = text, FocusMode = Control.FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 14);
        button.Pressed += onPressed;
        box.AddChild(button);
        return button;
    }
}
