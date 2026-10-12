using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

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

    /// <summary>A problem with one draft binding. Blocking ones prevent saving; warnings are saved anyway.</summary>
    public readonly record struct Conflict(string Message, bool Blocking);

    /// <summary>
    /// Draft entries with problems. <paramref name="editableTeammates"/> is how many teammate rows the menu
    /// lets you edit (the teammates actually in the game, or all of them outside a run):
    /// - reserved key, or a duplicate between two editable rows → blocking;
    /// - duplicate with a row you can't edit (no such teammate this game) → warning; the lowest teammate wins;
    /// - base key used by one of the game's own hotkeys → warning; the game's hotkey takes precedence.
    /// </summary>
    public static Dictionary<(int teammate, int slot), Conflict> DraftConflicts(int editableTeammates)
    {
        var conflicts = new Dictionary<(int, int), Conflict>();
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < PileViewer.SlotPiles.Length; s++)
            {
                if (Draft[t, s] is not KeyCombo combo) continue;
                if (Reserved.Contains(combo))
                {
                    conflicts[(t, s)] = new Conflict($"{combo} is reserved", Blocking: true);
                    continue;
                }
                Conflict? found = null;
                for (int t2 = 0; t2 < MaxTeammates && found is not { Blocking: true }; t2++)
                {
                    for (int s2 = 0; s2 < PileViewer.SlotPiles.Length; s2++)
                    {
                        if ((t2 == t && s2 == s) || Draft[t2, s2] != combo) continue;
                        string other = $"Teammate {t2 + 1} {PileViewer.PileName(PileViewer.SlotPiles[s2])}";
                        if (t < editableTeammates && t2 < editableTeammates)
                        {
                            found = new Conflict($"{combo} is also {other}", Blocking: true);
                            break;
                        }
                        int winner = Math.Min(t, t2) + 1;
                        found = new Conflict($"{combo} is also {other} (no such teammate this game) — Teammate {winner} gets it", Blocking: false);
                    }
                }
                if (found == null && NativeAction(combo.Key) is string action)
                {
                    found = new Conflict($"{OS.GetKeycodeString(combo.Key)} is the game's '{action}' hotkey, which takes precedence", Blocking: false);
                }
                if (found != null) conflicts[(t, s)] = found.Value;
            }
        }
        return conflicts;
    }

    /// <summary>Promotes the draft to active and saves, unless it has blocking conflicts. Returns whether it saved.</summary>
    public static bool TryCommitDraft(int editableTeammates)
    {
        if (DraftConflicts(editableTeammates).Values.Any(c => c.Blocking)) return false;
        Active = (KeyCombo?[,])Draft.Clone();
        Settings.Save();
        ModLog.Info("Hotkeys saved: " + string.Join(", ", Serialize().Select(kv => $"{kv.Key}={kv.Value}")));
        return true;
    }

    /// <summary>
    /// The game's own hotkey bound to this key, if any. The game matches its hotkeys on the key alone
    /// (NInputManager.ProcessHotkeyInput ignores modifiers), so Shift+S still fires its S hotkey.
    /// </summary>
    public static string? NativeAction(Key key)
    {
        try
        {
            object? manager = NInputManager.Instance;
            if (manager == null) return null;
            foreach (string field in new[] { "_mKbInputMap", "_fKbInputMap" })
            {
                if (Traverse.Create(manager).Field(field).GetValue() is Dictionary<StringName, Key> map)
                {
                    foreach (var (action, mapped) in map)
                    {
                        if (mapped == key) return action.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModLog.Error("Hotkeys.NativeAction", ex);
        }
        return null;
    }

    public static void ResetDraftToDefaults() => Draft = Defaults();

    /// <summary>If the key event matches an active binding, the (teammate, pile) it opens — the lowest teammate on duplicates.</summary>
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
