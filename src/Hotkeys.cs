using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace Spire2PileViewer;

/// <summary>A key or an extra mouse button, plus exact modifiers, e.g. "Ctrl+Shift+Z" or "Shift+Mouse 4".</summary>
public readonly record struct KeyCombo(Key Key, bool Ctrl, bool Shift, bool Alt, MouseButton Mouse = MouseButton.None)
{
    /// <summary>Mouse buttons that can be bound (not left/right/wheel, which the game needs).</summary>
    public static bool IsBindable(MouseButton button) => button is MouseButton.Middle or MouseButton.Xbutton1 or MouseButton.Xbutton2;

    public bool IsMouse => Mouse != MouseButton.None;

    private string Mods => (Ctrl ? "Ctrl+" : "") + (Shift ? "Shift+" : "") + (Alt ? "Alt+" : "");

    public override string ToString() => Mods + (Mouse switch
    {
        MouseButton.Middle => "Mouse 3",
        MouseButton.Xbutton1 => "Mouse 4",
        MouseButton.Xbutton2 => "Mouse 5",
        MouseButton.None => OS.GetKeycodeString(Key),
        _ => Mouse.ToString(),
    });

    public string Serialize() => Mods + (IsMouse ? "Mouse" + Mouse : Key.ToString());

    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Split('+');
        bool ctrl = parts.Contains("Ctrl"), shift = parts.Contains("Shift"), alt = parts.Contains("Alt");
        string last = parts[^1];
        if (last.StartsWith("Mouse") && Enum.TryParse(last["Mouse".Length..], out MouseButton button) && IsBindable(button))
        {
            return new KeyCombo(Key.None, ctrl, shift, alt, button);
        }
        if (!Enum.TryParse(last, out Key key)) return null;
        return new KeyCombo(key, ctrl, shift, alt);
    }

    public static KeyCombo From(InputEventKey ev) => new(ev.Keycode, ev.CtrlPressed, ev.ShiftPressed, ev.AltPressed);

    public static KeyCombo From(InputEventMouseButton ev) => new(Key.None, ev.CtrlPressed, ev.ShiftPressed, ev.AltPressed, ev.ButtonIndex);

    public bool Matches(InputEvent ev) => ev switch
    {
        InputEventKey key => !IsMouse && key.Keycode == Key && key.CtrlPressed == Ctrl && key.ShiftPressed == Shift && key.AltPressed == Alt,
        InputEventMouseButton mouse => IsMouse && mouse.ButtonIndex == Mouse && mouse.CtrlPressed == Ctrl && mouse.ShiftPressed == Shift && mouse.AltPressed == Alt,
        _ => false,
    };
}

/// <summary>
/// Hotkeys that open a teammate's pile. Bindings are per teammate *position* (teammate 1 = first other
/// player in party order, 2 = second, ...) and per pile. <see cref="Active"/> is what's in effect and
/// saved; the F1 menu edits <see cref="Draft"/> and only promotes it when there are no conflicts.
/// </summary>
public static class Hotkeys
{
    public const int MaxTeammates = 3;

    /// <summary>Per-teammate actions, in strip order: draw | hand (toggle their hand view) | exhaust | discard.</summary>
    public static readonly PileType[] Slots = PileViewer.StripSlots;

    /// <summary>Row after the teammates holding the one global binding (slot 0): Hide/show my hand. Always editable.</summary>
    public const int GlobalRow = MaxTeammates;

    private const int Rows = MaxTeammates + 1;

    public static string Describe(int row, int slot) => row == GlobalRow
        ? "Hide/show hand"
        : $"Teammate {row + 1} {PileViewer.PileName(Slots[slot])}";

    // Keys the game doesn't use; modifiers distinguish teammates. Hide/show hand is plain H.
    private static readonly Dictionary<PileType, Key> DefaultKeys = new()
    {
        [PileType.Draw] = Key.Z,
        [PileType.Hand] = Key.H,
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
        var map = new KeyCombo?[Rows, Slots.Length];
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < Slots.Length; s++)
            {
                if (!DefaultKeys.TryGetValue(Slots[s], out Key key)) continue;
                map[t, s] = t switch
                {
                    0 => new KeyCombo(key, Ctrl: false, Shift: true, Alt: false),
                    1 => new KeyCombo(key, Ctrl: true, Shift: false, Alt: false),
                    _ => new KeyCombo(key, Ctrl: true, Shift: true, Alt: false),
                };
            }
        }
        map[GlobalRow, 0] = new KeyCombo(Key.H, Ctrl: false, Shift: false, Alt: false);
        return map;
    }

    /// <summary>Bindings that belong to the Hand Viewer (each teammate's Hand slot and Hide/show hand).</summary>
    public static bool IsHandBinding(int row, int slot) => row == GlobalRow ? slot == 0 : Slots[slot] == PileType.Hand;

    /// <summary>Restores the defaults of the Hand Viewer bindings (hand = true) or the pile bindings, keeping the rest.</summary>
    public static void ResetDefaults(bool hand)
    {
        var defaults = Defaults();
        for (int t = 0; t < Rows; t++)
        {
            for (int s = 0; s < Slots.Length; s++)
            {
                if (IsHandBinding(t, s) == hand) Active[t, s] = defaults[t, s];
            }
        }
        Draft = (KeyCombo?[,])Active.Clone();
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
        bool Editable(int row) => row == GlobalRow || row < editableTeammates;
        for (int t = 0; t < Rows; t++)
        {
            for (int s = 0; s < Slots.Length; s++)
            {
                if (Draft[t, s] is not KeyCombo combo) continue;
                if (Reserved.Contains(combo))
                {
                    conflicts[(t, s)] = new Conflict($"{combo} is reserved", Blocking: true);
                    continue;
                }
                Conflict? found = null;
                for (int t2 = 0; t2 < Rows && found is not { Blocking: true }; t2++)
                {
                    for (int s2 = 0; s2 < Slots.Length; s2++)
                    {
                        if ((t2 == t && s2 == s) || Draft[t2, s2] != combo) continue;
                        string other = Describe(t2, s2);
                        if (Editable(t) && Editable(t2))
                        {
                            found = new Conflict($"{combo} is also {other}", Blocking: true);
                            break;
                        }
                        int winner = Math.Min(t, t2) + 1;
                        found = new Conflict($"{combo} is also {other} (Teammate {winner} gets it)", Blocking: false);
                    }
                }
                if (found == null && !combo.IsMouse && NativeAction(combo.Key) is string action)
                {
                    found = new Conflict($"{OS.GetKeycodeString(combo.Key)} is used by the game", Blocking: false);
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
            NInputManager? manager = NInputManager.Instance;
            if (manager == null) return null;
            // Only the map the game is using right now (same choice as NInputManager._UnhandledKeyInput):
            // the keyboard-only map in keyboard-only mode, otherwise the mouse+keyboard map. Checking both
            // made a key the player had moved off the mouse+keyboard map still count as taken.
            bool keyboardOnly = manager.ControllerManager?.InputType == InputType.KeyboardOnlyMode;
            string field = keyboardOnly ? "_fKbInputMap" : "_mKbInputMap";
            if (Traverse.Create(manager).Field(field).GetValue() is Dictionary<StringName, Key> map)
            {
                foreach (var (action, mapped) in map)
                {
                    if (mapped == key) return action.ToString();
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
    public static (int teammate, PileType pile)? Match(InputEvent ev)
    {
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < Slots.Length; s++)
            {
                if (Active[t, s] is KeyCombo combo && combo.Matches(ev)) return (t, Slots[s]);
            }
        }
        return null;
    }

    /// <summary>The Hide/show my hand binding matches this key event.</summary>
    public static bool MatchHideHand(InputEvent ev) => Active[GlobalRow, 0] is KeyCombo combo && combo.Matches(ev);

    public static Dictionary<string, string> Serialize()
    {
        var dict = new Dictionary<string, string>();
        for (int t = 0; t < MaxTeammates; t++)
        {
            for (int s = 0; s < Slots.Length; s++)
            {
                dict[$"teammate{t + 1}.{Slots[s]}"] = Active[t, s]?.Serialize() ?? "";
            }
        }
        dict["hideHand"] = Active[GlobalRow, 0]?.Serialize() ?? "";
        return dict;
    }

    public static void Deserialize(Dictionary<string, string>? dict)
    {
        Active = Defaults();
        if (dict != null)
        {
            for (int t = 0; t < MaxTeammates; t++)
            {
                for (int s = 0; s < Slots.Length; s++)
                {
                    if (dict.TryGetValue($"teammate{t + 1}.{Slots[s]}", out string? value))
                    {
                        Active[t, s] = KeyCombo.Parse(value);
                    }
                }
            }
            if (dict.TryGetValue("hideHand", out string? hide)) Active[GlobalRow, 0] = KeyCombo.Parse(hide);
        }
        Draft = (KeyCombo?[,])Active.Clone();
    }
}
