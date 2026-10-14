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
        // Offsets from the default spot next to each player's name (v0.3.2+; the old "stripOffsets" were relative to
        // the character and are ignored).
        public Dictionary<string, float[]>? stripPositions { get; set; }
        // Float alignment: offsets from the default spot above the character (same key the pre-v0.4 versions used).
        public Dictionary<string, float[]>? stripOffsets { get; set; }
        public string alignment { get; set; } = "fixed";
        public Dictionary<string, string>? hotkeys { get; set; }
        public bool handViewer { get; set; } = true;
        public bool hideHandButton { get; set; } = true;
        public float handShadow { get; set; } = 0.85f;
        public float handVisible { get; set; } = 0.20f;
        public float hoverDelay { get; set; } = 0.1f;
        public int maxHandViewers { get; set; } = 1;
        public float handShadowHidden { get; set; } = 0.15f;
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

    /// <summary>Where the Pile Viewers sit: Fixed = next to each name in the player list (fixed to the UI);
    /// Float = above each character (moves with the characters, which differ between fights).</summary>
    public enum PileViewerAlignment { Fixed, Float }

    public static PileViewerAlignment Alignment = PileViewerAlignment.Fixed;

    // ---- Hand Viewer (F1 → Hand Viewer tab) ----

    /// <summary>Hand Viewer on: strips get a Hand slot that shows that teammate's hand behind yours.</summary>
    public static bool HandViewerEnabled = true;

    /// <summary>Show the "Hide hand" button under End Turn.</summary>
    public static bool HideHandButtonEnabled = true;

    /// <summary>How dark the teammate's hand is drawn (0 = no shadow, 1 = darkest).</summary>
    public static float HandShadow = 0.85f;

    /// <summary>Seconds a teammate card must be hovered before its closeup shows (0 = instant, MaxHoverDelay = never).</summary>
    public static float HoverDelay = 0.1f;

    public const float MaxHoverDelay = 1f;

    /// <summary>How many teammates' hands can be shown at once (stacked behind yours); more closes the oldest.</summary>
    public static int MaxHandViewers = 1;

    /// <summary>Up to every teammate in a 4-player game.</summary>
    public const int MaxMaxHandViewers = 3;

    /// <summary>Drag offset of the "Hide hand" button from its default spot above the energy counter.</summary>
    public static Vector2 HideButtonOffset = Vector2.Zero;

    /// <summary>Teammate hand shadow while your own hand is hidden (lighter, since nothing covers it).</summary>
    public static float HandShadowWhenHidden = 0.15f;

    /// <summary>% of each teammate card visible above your own hand (0..1).</summary>
    public static float HandVisible = 0.20f;

    /// <summary>
    /// Per-strip drag offsets from the default spot next to each player's name in the player list, keyed by the
    /// player's NetId (their Steam id online), so each teammate's strip keeps its own position across sessions.
    /// </summary>
    private static readonly Dictionary<ulong, Vector2> _offsets = new();

    // Float alignment's own offsets (from the spot above the character).
    private static readonly Dictionary<ulong, Vector2> _floatOffsets = new();

    private static Dictionary<ulong, Vector2> CurrentOffsets => Alignment == PileViewerAlignment.Float ? _floatOffsets : _offsets;

    public static Vector2 GetOffset(ulong netId) => CurrentOffsets.TryGetValue(netId, out Vector2 v) ? v : Vector2.Zero;

    public static void SetOffset(ulong netId, Vector2 offset)
    {
        if (offset == Vector2.Zero) CurrentOffsets.Remove(netId);
        else CurrentOffsets[netId] = offset;
    }

    public static string DescribeOffsets() =>
        _offsets.Count == 0 ? "none" : string.Join(", ", _offsets.Select(kv => $"{kv.Key}={kv.Value}"));

#if DEBUG
    // Auto-test runs use their own file (reset at start) so they never touch the player's real settings.
    private static string FilePath => Path.Combine(OS.GetUserDataDir(),
        AutoTest.Enabled ? "spire2pileviewer_settings_autotest.json" : "spire2pileviewer_settings.json");
#else
    private static string FilePath => Path.Combine(OS.GetUserDataDir(), "spire2pileviewer_settings.json");
#endif

#if DEBUG
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
#endif

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
            foreach (var (key, xy) in dto.stripPositions ?? new Dictionary<string, float[]>())
            {
                if (ulong.TryParse(key, out ulong netId) && xy is { Length: 2 }) _offsets[netId] = new Vector2(xy[0], xy[1]);
            }
            _floatOffsets.Clear();
            foreach (var (key, xy) in dto.stripOffsets ?? new Dictionary<string, float[]>())
            {
                if (ulong.TryParse(key, out ulong netId) && xy is { Length: 2 }) _floatOffsets[netId] = new Vector2(xy[0], xy[1]);
            }
            Alignment = dto.alignment == "float" ? PileViewerAlignment.Float : PileViewerAlignment.Fixed;
            Hotkeys.Deserialize(dto.hotkeys);
            HandViewerEnabled = dto.handViewer;
            HideHandButtonEnabled = dto.hideHandButton;
            HandShadow = Math.Clamp(dto.handShadow, 0f, 1f);
            HandVisible = Math.Clamp(dto.handVisible, 0f, 1f);
            HandShadowWhenHidden = Math.Clamp(dto.handShadowHidden, 0f, 1f);
            HoverDelay = Math.Clamp(dto.hoverDelay, 0f, MaxHoverDelay);
            MaxHandViewers = Math.Clamp(dto.maxHandViewers, 1, MaxMaxHandViewers);
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
                stripPositions = _offsets.ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.X, kv.Value.Y }),
                stripOffsets = _floatOffsets.ToDictionary(kv => kv.Key.ToString(), kv => new[] { kv.Value.X, kv.Value.Y }),
                alignment = Alignment == PileViewerAlignment.Float ? "float" : "fixed",
                hotkeys = Hotkeys.Serialize(),
                handViewer = HandViewerEnabled,
                hideHandButton = HideHandButtonEnabled,
                handShadow = HandShadow,
                handVisible = HandVisible,
                handShadowHidden = HandShadowWhenHidden,
                hoverDelay = HoverDelay,
                maxHandViewers = MaxHandViewers,
                hideButtonOffset = HideButtonOffset == Vector2.Zero ? null : new[] { HideButtonOffset.X, HideButtonOffset.Y },
            };
            File.WriteAllText(FilePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ModLog.Error("Settings.Save", ex);
        }
    }

    public static void ResetStripPositions()
    {
        _offsets.Clear();
        _floatOffsets.Clear();
        Save();
        ModLog.Info("Settings: strip positions reset");
    }

    public static void ResetHideButtonPosition()
    {
        HideButtonOffset = Vector2.Zero;
        Save();
        ModLog.Info("Settings: Hide hand button position reset");
    }

    /// <summary>Pile Viewer tab defaults: strips, their positions and the pile hotkeys.</summary>
    public static void ResetPileViewer()
    {
        Enabled = true;
        Scale = 1f;
        Opacity = 0.75f;
        ShowSelf = false;
        TextList = false;
        _offsets.Clear();
        _floatOffsets.Clear();
        Alignment = PileViewerAlignment.Fixed;
        Hotkeys.ResetDefaults(hand: false);
        Save();
        ModLog.Info("Settings: Pile Viewer reset to defaults");
    }

    /// <summary>Hand Viewer tab defaults: its toggles, sliders, button position and hand hotkeys.</summary>
    public static void ResetHandViewer()
    {
        HandViewerEnabled = true;
        HideHandButtonEnabled = true;
        HandShadow = 0.85f;
        HandVisible = 0.20f;
        HandShadowWhenHidden = 0.15f;
        HoverDelay = 0.1f;
        MaxHandViewers = 1;
        HideButtonOffset = Vector2.Zero;
        Hotkeys.ResetDefaults(hand: true);
        Save();
        ModLog.Info("Settings: Hand Viewer reset to defaults");
    }

    public static void ResetAll()
    {
        ResetPileViewer();
        ResetHandViewer();
    }
}
