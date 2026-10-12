using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;

namespace Spire2PileViewer;

/// <summary>A key plus exact modifiers, e.g. "Ctrl+Shift+Z".</summary>
public readonly record struct KeyCombo(Key Key, bool Ctrl, bool Shift, bool Alt)
{
    public override string ToString() =>
        (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + OS.GetKeycodeString(Key);

    public string Serialize() => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "") + Key;

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Split('+');
        if (!Enum.TryParse(parts[^1], out Key key)) return null;
        return new KeyCombo(key, parts.Contains("Ctrl"), parts.Contains("Shift"), parts.Contains("Alt"));
    }

    public static KeyCombo From(InputEventKey ev) => new(ev.Keycode, ev.CtrlPressed, ev.ShiftPressed, ev.AltPressed);

    public bool Matches(InputEventKey ev) =>
        ev.Keycode == Key && ev.CtrlPressed == Ctrl && ev.ShiftPressed == Shift && ev.AltPressed == Alt;
}

/// <summary>
/// Hotkeys that open a teammate's pile. Bindings are per teammate *position* (teammate 1 = first other
/// player in party order, 2 = second, ...) and per pile. <see cref="Active"/> is what's in effect and
/// saved; the F1 menu edits <see cref="Draft"/> and only promotes it when there are no conflicts.
/// </summary>
public static class Hotkeys
{
    public const int MaxTeammates = 3;

    // Keys the game doesn't use; modifiers distinguish teammates.
    private static readonly Dictionary<PileType, Key> DefaultKeys = new()
    {
        [PileType.Draw] = Key.Z,
        [PileType.Exhaust] = Key.V,
        [PileType.Discard] = Key.C,
    };

    /// <summary>Plain keys other menus already use: this mod's and BetterSpire2's F1 menu, BetterSpire2's F3 hand viewer.</summary>
    private static readonly KeyCombo[] Reserved =
    {
        new(Key.F1, false, false, false),
        new(Key.F3, false, false, false),
        new(Key.Escape, false, false, false),
    };

    public static KeyCombo?[,] Active = Defaults();

    public static KeyCombo?[,] Draft = (KeyCombo?[,])Active.Clone();

    public static KeyCombo?[,] Defaults()
    {
        var map = new KeyCombo?[MaxTeammates, PileViewer.SlotPiles.Length];
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
            {
                Key key = DefaultKeys[PileViewer.SlotPiles[s]];
                map[t, s] = t switch
                {
                    0 => new KeyCombo(key, Ctrl: false, Shift: true, Alt: false),
                    1 => new KeyCombo(key, Ctrl: true, Shift: false, Alt: false),
                    _ => new KeyCombo(key, Ctrl: true, Shift: true, Alt: false),
                };
            }
        }
        return map;
    }

    /// <summary>Draft entries that clash with another entry or a reserved key, with a reason for each.</summary>
    public static Dictionary<(int teammate, int slot), string> DraftConflicts()
    {
        var conflicts = new Dictionary<(int, int), string>();
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
            {
                if (Draft[t, s] is not KeyCombo combo) continue;
                if (Reserved.Contains(combo))
                {
                    conflicts[(t, s)] = $"{combo} is reserved";
                    continue;
                }
                for (int t2 = 0; t2 < MaxTeammates; t2++)
                {
                    for (int s2 = 0; s2 < PileViewer.SlotPiles.Length; s2++)
                    {
                        if ((t2 != t || s2 != s) && Draft[t2, s2] == combo)
                        {
                            conflicts[(t, s)] = $"{combo} is also Teammate {t2 + 1} {PileViewer.PileName(PileViewer.SlotPiles[s2])}";
                        }
                    }
                }
            }
        }
        return conflicts;
    }

    /// <summary>Promotes the draft to active and saves, unless it has conflicts. Returns whether it saved.</summary>
    public static bool TryCommitDraft()
    {
        if (DraftConflicts().Count > 0) return false;
        Active = (KeyCombo?[,])Draft.Clone();
        Settings.Save();
        ModLog.Info("Hotkeys saved: " + string.Join(", ", Serialize().Select(kv => $"{kv.Key}={kv.Value}")));
        return true;
    }

    public static void ResetDraftToDefaults() => Draft = Defaults();

    /// <summary>If the key event matches an active binding, the (teammate, pile) it opens.</summary>
    public static (int teammate, PileType pile)? Match(InputEventKey ev)
    {
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
            {
                if (Active[t, s] is KeyCombo combo && combo.Matches(ev)) return (t, PileViewer.SlotPiles[s]);
            }
        }
        return null;
    }

    public static Dictionary<string, string> Serialize()
    {
        var dict = new Dictionary<string, string>();
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
            {
                dict[$"teammate{t + 1}.{PileViewer.SlotPiles[s]}"] = Active[t, s]?.Serialize() ?? "";
            }
        }
        return dict;
    }

    public static void Deserialize(Dictionary<string, string>? dict)
    {
        Active = Defaults();
        if (dict != null)
        {
            for (int t = 0; t < MaxTeammates; t++)
            {
                for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
                {
                    if (dict.TryGetValue($"teammate{t + 1}.{PileViewer.SlotPiles[s]}", out string? value))
                    {
                        Active[t, s] = KeyCombo.Parse(value);
                    }
                }
            }
        }
        Draft = (KeyCombo?[,])Active.Clone();
    }
}
