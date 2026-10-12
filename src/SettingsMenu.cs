using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace Spire2PileViewer;

/// <summary>
/// F1 settings panel. BetterSpire2 also opens its menu on F1 (centered), so this one docks to the
/// top-right to sit beside it. Every change is saved immediately.
/// </summary>
public static class SettingsMenu
{
    public const Key Hotkey = Key.F1;

    private const float PanelWidth = 340f;

    private static CanvasLayer? _layer;

    // Exposed for the auto-test, which drives the menu through its real controls.
    internal static CheckButton? EnabledToggle;
    internal static HSlider? ScaleSlider;
    internal static HSlider? OpacitySlider;
    internal static CheckButton? ShowSelfToggle;
    internal static CheckButton? TextListToggle;
    internal static Button? ResetPositionButton;
    internal static Button? ResetAllButton;

    public static bool IsOpen => _layer != null && GodotObject.IsInstanceValid(_layer);

    [HarmonyPatch(typeof(NGame), "_Input")]
    public static class InputPatch
    {
        private static void Postfix(InputEvent inputEvent)
        {
            try
            {
                if (inputEvent is InputEventKey { Pressed: true } key && !key.IsEcho() && key.Keycode == Hotkey
                    && !key.ShiftPressed && !key.CtrlPressed && !key.AltPressed)
                {
                    Toggle();
                }
            }
            catch (Exception ex)
            {
                ModLog.Error("SettingsMenu.InputPatch", ex);
            }
        }
    }

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public static void Close()
    {
        if (IsOpen) _layer!.QueueFree();
        _layer = null;
    }

    public static void Open()
    {
        Close();
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

        EnabledToggle = AddToggle(box, "Show teammates' pile strips", Settings.Enabled, v => Settings.Enabled = v);
        ScaleSlider = AddSlider(box, "Strip size", Settings.MinScale * 100f, Settings.MaxScale * 100f, Settings.Scale * 100f,
            v => Settings.Scale = (float)v / 100f);
        OpacitySlider = AddSlider(box, "Strip background opacity", 0f, 100f, Settings.Opacity * 100f,
            v => Settings.Opacity = (float)v / 100f);
        ShowSelfToggle = AddToggle(box, "Also show a strip over my character", Settings.ShowSelf, v => Settings.ShowSelf = v);
        TextListToggle = AddToggle(box, "Open piles as a text list instead of the card screen", Settings.TextList,
            v => Settings.TextList = v);

        ResetPositionButton = AddButton(box, "Reset strip position", Settings.ResetPosition);
        ResetAllButton = AddButton(box, "Reset all settings", () =>
        {
            Settings.ResetAll();
            Open(); // rebuild so the controls show the defaults
        });

        var hint = new Label
        {
            Text = "Click a slot to view that pile. Shift+drag a strip to move all strips. F1 closes this menu.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(PanelWidth - 28f, 0f),
        };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", new Color(0.6f, 0.6f, 0.6f));
        box.AddChild(hint);

        Vector2 viewport = game.GetViewport()?.GetVisibleRect().Size ?? new Vector2(1920f, 1080f);
        panel.Position = new Vector2(viewport.X - PanelWidth - 40f, 110f);
        ModLog.Info("SettingsMenu: opened");
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
