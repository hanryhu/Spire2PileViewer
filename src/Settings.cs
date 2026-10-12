using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public Dictionary<string, float[]>? stripOffsets { get; set; }
        public Dictionary<string, string>? hotkeys { get; set; }
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

    /// <summary>
    /// Per-strip drag offsets relative to the default spot above the character, keyed by the player's NetId
    /// (their Steam id online), so each teammate's strip keeps its own position across sessions.
    /// </summary>
    private static readonly Dictionary<ulong, Vector2> _offsets = new();

    public static Vector2 GetOffset(ulong netId) => _offsets.TryGetValue(netId, out Vector2 v) ? v : Vector2.Zero;

    public static void SetOffset(ulong netId, Vector2 offset)
    {
        if (offset == Vector2.Zero) _offsets.Remove(netId);
        else _offsets[netId] = offset;
    }

    public static string DescribeOffsets() =>
        _offsets.Count == 0 ? "none" : string.Join(", ", _offsets.Select(kv => $"{kv.Key}={kv.Value}"));

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
            _offsets.Clear();
            foreach (var (key, xy) in dto.stripOffsets ?? new Dictionary<string, float[]>())
            {
                if (ulong.TryParse(key, out ulong netId) && xy is { Length: 2 }) _offsets[netId] = new Vector2(xy[0], xy[1]);
            }
            Hotkeys.Deserialize(dto.hotkeys);
            ModLog.Info($"Settings loaded: enabled={Enabled} scale={Scale} opacity={Opacity} showSelf={ShowSelf} textList={TextList} offsets={DescribeOffsets()}");
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
                stripOffsets = _offsets.ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.X, kv.Value.Y }),
                hotkeys = Hotkeys.Serialize(),
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings.Save", ex);
        }
    }

    public static void ResetPositions()
    {
        _offsets.Clear();
        Save();
        ModLog.Info("Settings: strip positions reset");
    }

    public static void ResetAll()
    {
        Enabled = true;
        Scale = 1f;
        Opacity = 0.75f;
        ShowSelf = false;
        TextList = false;
        _offsets.Clear();
        Hotkeys.Deserialize(null);
        Save();
        ModLog.Info("Settings: all reset to defaults");
    }
}
