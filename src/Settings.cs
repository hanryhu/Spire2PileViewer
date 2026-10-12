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
        public bool handViewer { get; set; } = true;
        public bool hideHandButton { get; set; } = true;
        public float handShadow { get; set; } = 0.5f;
        public float handVisible { get; set; } = 0.25f;
        public float[]? hideButtonOffset { get; set; }
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

    // ---- Hand Viewer (F1 → Hand Viewer tab) ----

    /// <summary>Hand Viewer on: strips get a Hand slot that shows that teammate's hand behind yours.</summary>
    public static bool HandViewerEnabled = true;

    /// <summary>Show the "Hide hand" button under End Turn.</summary>
    public static bool HideHandButtonEnabled = true;

    /// <summary>How dark the teammate's hand is drawn (0 = no shadow, 1 = darkest).</summary>
    public static float HandShadow = 0.5f;

    /// <summary>Drag offset of the "Hide hand" button from its default spot above the energy counter.</summary>
    public static Vector2 HideButtonOffset = Vector2.Zero;

    /// <summary>% of each teammate card visible above your own hand (0..1).</summary>
    public static float HandVisible = 0.25f;

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
            HandViewerEnabled = dto.handViewer;
            HideHandButtonEnabled = dto.hideHandButton;
            HandShadow = Math.Clamp(dto.handShadow, 0f, 1f);
            HandVisible = Math.Clamp(dto.handVisible, 0f, 1f);
            HideButtonOffset = dto.hideButtonOffset is { Length: 2 } o ? new Vector2(o[0], o[1]) : Vector2.Zero;
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
                handViewer = HandViewerEnabled,
                hideHandButton = HideHandButtonEnabled,
                handShadow = HandShadow,
                handVisible = HandVisible,
                hideButtonOffset = HideButtonOffset == Vector2.Zero ? null : new[] { HideButtonOffset.X, HideButtonOffset.Y },
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
        HideButtonOffset = Vector2.Zero;
        Save();
        ModLog.Info("Settings: strip and Hide hand button positions reset");
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
        HandViewerEnabled = true;
        HideHandButtonEnabled = true;
        HandShadow = 0.5f;
        HandVisible = 0.25f;
        HideButtonOffset = Vector2.Zero;
        Save();
        ModLog.Info("Settings: all reset to defaults");
    }
}
