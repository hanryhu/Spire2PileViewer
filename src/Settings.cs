using System;
using System.IO;
using System.Text.Json;
using Godot;

namespace Spire2PileViewer;

/// <summary>
/// Persisted settings in %APPDATA%\SlayTheSpire2\spire2pileviewer_settings.json (same folder BetterSpire2
/// keeps its layout in). Missing or unreadable file → defaults. Edited from the F1 menu (SettingsMenu).
/// </summary>
public static class Settings
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 2.0f;

    private sealed class Dto
    {
        public bool enabled { get; set; } = true;
        public float scale { get; set; } = 1f;
        public float opacity { get; set; } = 0.75f;
        public bool showSelf { get; set; }
        public bool textList { get; set; }
        public float offsetX { get; set; }
        public float offsetY { get; set; }
    }

    /// <summary>Show the strips at all.</summary>
    public static bool Enabled = true;

    /// <summary>Strip size multiplier (MinScale..MaxScale).</summary>
    public static float Scale = 1f;

    /// <summary>Strip background opacity (0 = only icons, 1 = solid).</summary>
    public static float Opacity = 0.75f;

    /// <summary>Also show a strip over your own character.</summary>
    public static bool ShowSelf;

    /// <summary>Clicking a slot shows a text list above the strip instead of the game's card screen.</summary>
    public static bool TextList;

    /// <summary>Drag offset applied to every strip, relative to its default spot above the character.</summary>
    public static Vector2 Offset = Vector2.Zero;

    // Auto-test runs use their own file (reset at start) so they never touch the player's real settings.
    private static string FilePath => Path.Combine(OS.GetUserDataDir(),
        AutoTest.Enabled ? "spire2pileviewer_settings_autotest.json" : "spire2pileviewer_settings.json");

    /// <summary>Auto-test only: start from defaults.</summary>
    internal static void ResetForAutoTest()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings.ResetForAutoTest", ex);
        }
        ResetAll();
    }

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            Dto? dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto == null) return;
            Enabled = dto.enabled;
            Scale = Math.Clamp(dto.scale, MinScale, MaxScale);
            Opacity = Math.Clamp(dto.opacity, 0f, 1f);
            ShowSelf = dto.showSelf;
            TextList = dto.textList;
            Offset = new Vector2(dto.offsetX, dto.offsetY);
            ModLog.Info($"Settings loaded: enabled={Enabled} scale={Scale} opacity={Opacity} showSelf={ShowSelf} textList={TextList} offset={Offset}");
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings.Load", ex);
        }
    }

    public static void Save()
    {
        try
        {
            var dto = new Dto
            {
                enabled = Enabled,
                scale = Scale,
                opacity = Opacity,
                showSelf = ShowSelf,
                textList = TextList,
                offsetX = Offset.X,
                offsetY = Offset.Y,
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings.Save", ex);
        }
    }

    public static void ResetPosition()
    {
        Offset = Vector2.Zero;
        Save();
        ModLog.Info("Settings: strip position reset");
    }

    public static void ResetAll()
    {
        Enabled = true;
        Scale = 1f;
        Opacity = 0.75f;
        ShowSelf = false;
        TextList = false;
        Offset = Vector2.Zero;
        Save();
        ModLog.Info("Settings: all reset to defaults");
    }
}
