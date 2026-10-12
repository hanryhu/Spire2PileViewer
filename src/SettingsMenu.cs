using System;
using System.Linq;
using Godot;
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
    private static readonly Button?[] _hotkeyButtons = new Button?[3];
    private static Label? _hotkeyStatus;

    // Exposed for the auto-test, which drives the menu through its real controls.
    internal static CheckButton? EnabledToggle;
    internal static HSlider? ScaleSlider;
    internal static HSlider? OpacitySlider;
    internal static CheckButton? ShowSelfToggle;
    internal static CheckButton? TextListToggle;
    internal static Button? ResetPositionButton;
    internal static Button? ResetAllButton;
    internal static OptionButton? TeammatePicker;
    internal static Button? GetHotkeyButton(int slot) => _hotkeyButtons[slot];
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
                KeyCombo? combo = key.Keycode is Key.Backspace or Key.Delete ? null : KeyCombo.From(key);
                Hotkeys.Draft[_selectedTeammate, slot] = combo;
                ModLog.Info($"SettingsMenu: Teammate {_selectedTeammate + 1} {PileViewer.SlotPiles[slot]} = {combo?.ToString() ?? "unbound"}");
                Hotkeys.TryCommitDraft(_editableTeammates);
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
                if (Hotkeys.Match(key) is not { } hit) return;
                if (Hotkeys.NativeAction(key.Keycode) is string action)
                {
                    ModLog.Info($"Hotkey {KeyCombo.From(key)} skipped: the game's '{action}' uses {key.Keycode}");
                    return;
                }
                if (PileViewer.OpenTeammatePile(hit.teammate, hit.pile)) viewport.SetInputAsHandled();
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

        EnabledToggle = AddToggle(box, "Show teammates' piles", Settings.Enabled, v => Settings.Enabled = v);
        ScaleSlider = AddSlider(box, "Strip size", Settings.MinScale * 100f, Settings.MaxScale * 100f, Settings.Scale * 100f,
            v => Settings.Scale = (float)v / 100f);
        OpacitySlider = AddSlider(box, "Strip background opacity", 0f, 100f, Settings.Opacity * 100f,
            v => Settings.Opacity = (float)v / 100f);
        ShowSelfToggle = AddToggle(box, "Also show a strip over my character", Settings.ShowSelf, v => Settings.ShowSelf = v);
        TextListToggle = AddToggle(box, "Open piles as a text list instead of the card screen", Settings.TextList,
            v => Settings.TextList = v);

        AddHotkeysSection(box);

        ResetPositionButton = AddButton(box, "Reset strip positions", Settings.ResetPositions);
        ResetAllButton = AddButton(box, "Reset all settings", () =>
        {
            Settings.ResetAll();
            Open(); // rebuild so the controls show the defaults
        });

        var hint = new Label
        {
            Text = "Click a slot to view that pile. Shift+drag a strip to move it. F1 closes this menu.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 0f),
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        box.AddChild(hint);

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

        for (int slot = 0; slot < PileViewer.SlotPiles.Length; slot++)
        {
            var row = new HBoxContainer();
            box.AddChild(row);
            var label = new Label { Text = PileViewer.PileName(PileViewer.SlotPiles[slot]), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
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

        AddButton(box, "Restore default hotkeys", () =>
        {
            Hotkeys.ResetDraftToDefaults();
            Hotkeys.TryCommitDraft(_editableTeammates);
            RefreshHotkeys();
        });
    }

    private static void RefreshHotkeys()
    {
        if (!IsOpen || _hotkeyStatus == null) return;
        var conflicts = Hotkeys.DraftConflicts(_editableTeammates);
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
            _hotkeyStatus.Text = "Press a key combination. Esc cancels, Backspace clears.";
            _hotkeyStatus.AddThemeColorOverride("font_color", new Color(0.9f, 0.9f, 0.9f));
        }
        else if (conflicts.Any(c => c.Value.Blocking))
        {
            var first = conflicts.First(c => c.Value.Blocking);
            _hotkeyStatus.Text = $"Not saved — conflict: Teammate {first.Key.teammate + 1} " +
                $"{PileViewer.PileName(PileViewer.SlotPiles[first.Key.slot])}: {first.Value.Message}. Rebind to resolve.";
            _hotkeyStatus.AddThemeColorOverride("font_color", ConflictColor);
        }
        else if (conflicts.Count > 0)
        {
            // Prefer a warning about the teammate being viewed.
            var first = conflicts.OrderBy(c => c.Key.teammate == _selectedTeammate ? 0 : 1).First();
            _hotkeyStatus.Text = $"Saved, but note — Teammate {first.Key.teammate + 1} " +
                $"{PileViewer.PileName(PileViewer.SlotPiles[first.Key.slot])}: {first.Value.Message}.";
            _hotkeyStatus.AddThemeColorOverride("font_color", WarningColor);
        }
        else
        {
            _hotkeyStatus.Text = "Saved. Hotkeys open the selected teammate's piles during combat.";
            _hotkeyStatus.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        }
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

    private static HSlider AddSlider(VBoxContainer box, string text, float min, float max, float value, Action<double> apply)
    {
        var row = new HBoxContainer();
        box.AddChild(row);
        var label = new Label { Text = text, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        label.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(label);
        var valueLabel = new Label { Text = $"{value:0}%", CustomMinimumSize = new Vector2(48f, 0f), HorizontalAlignment = HorizontalAlignment.Right };
        valueLabel.AddThemeFontSizeOverride("font_size", 14);
        row.AddChild(valueLabel);

        var slider = new HSlider
        {
            MinValue = min,
            MaxValue = max,
            Step = 5,
            Value = value,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 20f),
        };
        slider.ValueChanged += v =>
        {
            apply(v);
            valueLabel.Text = $"{v:0}%";
            Settings.Save();
        };
        box.AddChild(slider);
        return slider;
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
