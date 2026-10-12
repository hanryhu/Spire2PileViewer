using System;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>
/// Development-only smoke test, inert unless the game is launched with -pileviewerautotest.
///
/// Solo (no -fastmp): from the main menu, starts an unsaved run, jumps into a weak fight, and shows a
/// strip over the local player.
/// Multiplayer (with -fastmp host_standard / join): in the lobby both players pick a character and ready
/// up automatically (host waits for the client), the host then sends a networked "fight" so the whole
/// party enters combat, and each instance exercises its teammate's strip.
///
/// Every step is logged as "AutoTest: ..." ending in "AutoTest: done", so the mod can be checked from
/// the per-instance log files and window screenshots without anyone clicking through the game.
/// </summary>
public static class AutoTest
{
    public static readonly bool Enabled = OS.GetCmdlineArgs().Contains("-pileviewerautotest");

    /// <summary>
    /// -pileviewershowcase (with -pileviewerautotest): stage README screenshots instead of testing. Every player
    /// puts cards in their discard and exhaust piles, then the first client logs "SHOT 1/2/3" (default view, F1 menu,
    /// a teammate's pile screen) and holds each for a few seconds so an external script can capture the window.
    /// </summary>
    private static readonly bool Showcase = OS.GetCmdlineArgs().Contains("-pileviewershowcase");

    private static async Task RunShowcase(bool isHost)
    {
        Settings.ResetForAutoTest();
        for (int i = 0; i < 120 && !CombatManager.Instance.IsInProgress; i++) await Wait(0.25);
        await Wait(3.0);

        // Fill everyone's piles a little (networked, so every client sees the same counts).
        RunState runState = RunManager.Instance.DebugOnlyGetState()!;
        Player me = LocalContext.GetMe(runState)!;
        var hand = PileType.Hand.GetPile(me).Cards.Select(c => c.Id.Entry).ToList();
        int extraDiscards = 1 + Math.Max(ClientIndex, 0) % 2;
        var commands = Enumerable.Range(0, extraDiscards + 1).Select(i => $"card {hand[i % hand.Count]} Discard")
            .Append($"card {hand[^1]} Exhaust");
        foreach (string cmd in commands)
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: true));
            await Wait(0.7);
        }
        await Wait(3.0);
        Log("showcase: piles filled — " + PileViewer.DescribeState());
        // Shots come from the first client's window: every player's piles (its own native ones included) are filled there.
        if (ClientIndex != 0)
        {
            Log("done");
            return;
        }

        Log("SHOT 1");
        await Wait(6.0);
        SettingsMenu.Open();
        await Wait(1.0);
        Log("SHOT 2");
        await Wait(6.0);
        SettingsMenu.Close();
        // Open each of the teammate's piles once so every caption gets logged, ending on discard for the shot.
        foreach (PileType pile in new[] { PileType.Draw, PileType.Exhaust })
        {
            PileViewer.OpenTeammatePile(0, pile);
            await Wait(1.0);
            PileViewer.OpenTeammatePile(0, pile);
            await Wait(0.5);
        }
        PileViewer.OpenTeammatePile(0, PileType.Discard);
        await Wait(1.5);
        Log("SHOT 3 " + PileViewer.DescribeOpenView());
        await Wait(6.0);
        Log("done");
    }

    private static readonly bool Multiplayer = OS.GetCmdlineArgs().Contains("-fastmp");

    /// <summary>-pileviewerplayers N: party size the host waits for (default 2).</summary>
    private static readonly int ExpectedPlayers = ArgValue("-pileviewerplayers", 2);

    /// <summary>0 for the first client (-clientId 1000), 1 for the next, ...; -1 for the host.</summary>
    private static readonly int ClientIndex = ArgValue("-clientId", 999) - 1000;

    /// <summary>
    /// -autotestsuite piles|hands: which feature to exercise (default piles = the Pile Viewer checks).
    /// "hands" is the Hand Viewer suite (see HAND_VIEWER.md); it grows with each Hand Viewer milestone.
    /// </summary>
    private static readonly string Suite = ArgString("-autotestsuite", "piles");

    private static string ArgString(string name, string fallback)
    {
        string[] args = OS.GetCmdlineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    private static int ArgValue(string name, int fallback)
    {
        string[] args = OS.GetCmdlineArgs();
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v) ? v : fallback;
    }

    private static bool _started;

    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    public static class MainMenuReadyPatch
    {
        private static void Postfix()
        {
            if (!Enabled || Multiplayer || _started) return;
            _started = true;
            TaskHelper.RunSafely(RunSolo());
        }
    }

    [HarmonyPatch(typeof(NCharacterSelectScreen), "AfterInitialized")]
    public static class LobbyReadyPatch
    {
        private static void Postfix(NCharacterSelectScreen __instance)
        {
            if (!Enabled || !Multiplayer || _started) return;
            if (__instance.Lobby.NetService.Type == NetGameType.Singleplayer) return;
            _started = true;
            TaskHelper.RunSafely(RunMultiplayer(__instance.Lobby));
        }
    }

    private static async Task RunSolo()
    {
        try
        {
            Log("solo: starting");
            await Wait(2.0);
            CharacterModel character = ModelDb.AllCharacters.First();
            await NGame.Instance!.StartNewSingleplayerRun(character, shouldSave: false, ActModel.GetDefaultList().ToList(),
                Array.Empty<ModifierModel>(), SeedHelper.GetRandomSeed(), GameMode.Standard);
            Log($"run started as {character.Id.Entry}");
            await Wait(3.0);

            EncounterModel encounter = PickEncounter().ToMutable();
            encounter.DebugRandomizeRng();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter);
            Log($"entered encounter {encounter.Id.Entry}");

            if (Suite == "hands") await ExerciseHands(mutator: true, observer: true);
            else await ExerciseStrips(mutator: true, observer: true);
        }
        catch (Exception ex)
        {
            ModLog.Error("AutoTest", ex);
        }
    }

    private static async Task RunMultiplayer(StartRunLobby lobby)
    {
        try
        {
            bool isHost = lobby.NetService.Type == NetGameType.Host;
            Log($"multiplayer: in lobby as {(isHost ? "host" : "client")} (netId {lobby.NetService.NetId})");
            await Wait(2.0);

            if (isHost)
            {
                for (int i = 0; i < 240 && lobby.Players.Count < ExpectedPlayers; i++) await Wait(0.5);
                Log($"lobby players: {lobby.Players.Count}");
                await Wait(2.0); // let the clients pick and ready first
            }

            CharacterModel character = ModelDb.AllCharacters.ElementAt(isHost ? 0 : 1 + ClientIndex);
            lobby.SetLocalCharacter(character);
            await Wait(1.0);
            lobby.SetReady(ready: true);
            Log($"ready as {character.Id.Entry}");

            for (int i = 0; i < 240 && RunManager.Instance.DebugOnlyGetState() == null; i++) await Wait(0.5);
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Log($"run in progress: {runState != null} ({runState?.Players.Count ?? 0} players)");
            if (runState == null) return;
            await Wait(5.0);

            if (isHost)
            {
                Player me = LocalContext.GetMe(runState) ?? throw new InvalidOperationException("no local player");
                string cmd = "fight " + PickEncounter().Id.Entry;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: false));
                Log($"host enqueued '{cmd}'");
            }

            if (Showcase) await RunShowcase(isHost);
            else if (Suite == "hands") await ExerciseHands(mutator: ClientIndex == 0, observer: isHost);
            else await ExerciseStrips(mutator: ClientIndex == 0, observer: isHost);
        }
        catch (Exception ex)
        {
            ModLog.Error("AutoTest", ex);
        }
    }

    /// <param name="mutator">This instance changes its own piles (client in multiplayer, the player in solo).</param>
    /// <param name="observer">This instance's strip watches the mutator (host in multiplayer, the player in solo).</param>
    private static async Task ExerciseStrips(bool mutator, bool observer)
    {
        Settings.ResetForAutoTest();
        for (int i = 0; i < 120 && !CombatManager.Instance.IsInProgress; i++) await Wait(0.25);
        Log($"combat in progress: {CombatManager.Instance.IsInProgress}");
        await Wait(3.0);

        // --- Slots open the right pile (milestone 3) ---
        Log("state: " + PileViewer.DescribeState());
        foreach (PileType pile in PileViewer.SlotPiles)
        {
            PileViewer.SimulatePress(pile);
            await Wait(1.5);
            Log($"after {pile}: {PileViewer.DescribeOpenView()}");
        }
        // The empty Exhaust/Discard presses closed the screen, so: open Draw, then press Draw again to toggle it closed.
        PileViewer.SimulatePress(PileType.Draw);
        await Wait(1.5);
        Log($"after Draw (open): {PileViewer.DescribeOpenView()}");
        PileViewer.SimulatePress(PileType.Draw);
        await Wait(1.5);
        Log($"after Draw (toggle): {PileViewer.DescribeOpenView()}");

        // --- Live updates while the teammate's piles change ---
        // Both instances reach this point at about the same time; the mutator changes its piles every 2s
        // through networked console commands (the game's real CardPileCmd paths), the observer samples its strip.
        Task watch = observer ? WatchCounts(10) : Task.CompletedTask;
        if (mutator)
        {
            await Wait(1.0);
            RunState runState = RunManager.Instance.DebugOnlyGetState()!;
            Player me = LocalContext.GetMe(runState)!;
            string cardId = PileType.Hand.GetPile(me).Cards.First().Id.Entry;
            foreach (string cmd in new[] { "draw 2", $"card {cardId} Discard", $"card {cardId} Exhaust" })
            {
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: true));
                Log($"mutator enqueued '{cmd}'");
                await Wait(2.0);
            }
        }
        await watch;

        // --- F1 menu, driven through its real controls (milestone 4) ---
        SettingsMenu.Open();
        await Wait(0.5);
        SettingsMenu.ScaleSlider!.Value = 150;
        await Wait(0.5);
        Log($"menu: scale 150% → settings.scale={Settings.Scale} state: {PileViewer.DescribeState()}");
        SettingsMenu.OpacitySlider!.Value = 30;
        SettingsMenu.EnabledToggle!.ButtonPressed = false;
        await Wait(0.5);
        Log($"menu: disabled → {PileViewer.DescribeState()}");
        SettingsMenu.EnabledToggle.ButtonPressed = true;
        await Wait(0.5);
        Log($"menu: re-enabled → {PileViewer.DescribeState()}");

        // --- Shift+drag moves only that strip (others stay put); the menu's reset button puts them all back ---
        Log($"before drag: state: {PileViewer.DescribeState()}");
        PileViewer.SimulateDrag(new Vector2(-40f, -60f));
        await Wait(0.5);
        Log($"after drag (first strip only): offsets={Settings.DescribeOffsets()} state: {PileViewer.DescribeState()}");
        SettingsMenu.ResetPositionButton!.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.5);
        Log($"after menu reset: offsets={Settings.DescribeOffsets()} state: {PileViewer.DescribeState()}");
        Log($"settings file: scale={Settings.Scale} opacity={Settings.Opacity} enabled={Settings.Enabled}");

        await ExerciseHotkeys();
        Log("done — screenshot window now (menu left open)");
    }

    /// <summary>Milestone 5: pile hotkeys and native-style rebinding with conflict blocking, through real key events.</summary>
    private static async Task ExerciseHotkeys()
    {
        var teammates = PileViewer.Teammates();
        Log($"hotkeys: teammates = {string.Join(", ", teammates.Select(t => $"P{t.number} {t.player.Character.Id.Entry} ({t.player.NetId})"))}");
        Log($"hotkeys: picker items = {string.Join(" | ", Enumerable.Range(0, SettingsMenu.TeammatePicker!.ItemCount).Select(i => SettingsMenu.TeammatePicker.GetItemText(i)))}");

        await PressKey(Key.Z, shift: true);
        Log($"hotkeys: Shift+Z → {PileViewer.DescribeOpenView()}");
        await PressKey(Key.Z, shift: true);
        Log($"hotkeys: Shift+Z again → {PileViewer.DescribeOpenView()}");
        if (teammates.Count >= 2)
        {
            await PressKey(Key.Z, ctrl: true);
            Log($"hotkeys: Ctrl+Z (teammate 2 draw) → {PileViewer.DescribeOpenView()}");
            await PressKey(Key.Z, ctrl: true);
        }

        // Rebind teammate 1's draw to Shift+C (already discard) → conflict, nothing saved.
        SettingsMenu.TeammatePicker.Select(0);
        SettingsMenu.GetHotkeyButton(0)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressKey(Key.C, shift: true);
        Log($"hotkeys: draw→Shift+C: button='{SettingsMenu.GetHotkeyButton(0)!.Text}' status='{SettingsMenu.HotkeyStatus}' active draw={Hotkeys.Active[0, 0]}");
        // Rebind discard to Shift+B → conflict resolved → saved.
        SettingsMenu.GetHotkeyButton(2)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressKey(Key.B, shift: true);
        Log($"hotkeys: discard→Shift+B: status='{SettingsMenu.HotkeyStatus}' active draw={Hotkeys.Active[0, 0]} discard={Hotkeys.Active[0, 2]}");
        SettingsMenu.Close();
        await PressKey(Key.C, shift: true);
        Log($"hotkeys: Shift+C (new draw binding) → {PileViewer.DescribeOpenView()}");
        await PressKey(Key.C, shift: true);
        Log($"hotkeys: Shift+C again → {PileViewer.DescribeOpenView()}");

        // Milestone 6: hotkeys rank below the game.
        // (a) Dev console open → our hotkeys stay quiet.
        NDevConsole.Instance.ShowConsole();
        await Wait(0.5);
        await PressKey(Key.C, shift: true);
        Log($"hotkeys: Shift+C with console open → {PileViewer.DescribeOpenView()}");
        NDevConsole.Instance.HideConsole();
        await Wait(0.5);

        // (b) A binding on a key the game uses (S = discard pile) saves with a warning, and the game keeps the key.
        SettingsMenu.Open();
        SettingsMenu.TeammatePicker!.Select(0);
        SettingsMenu.GetHotkeyButton(0)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressKey(Key.S, shift: true);
        Log($"hotkeys: draw→Shift+S: status='{SettingsMenu.HotkeyStatus}' active draw={Hotkeys.Active[0, 0]}");
        SettingsMenu.Close();
        await PressKey(Key.S, shift: true);
        Log($"hotkeys: Shift+S → {PileViewer.DescribeOpenView()} (game's own pile expected, not the teammate's)");
        if (PileViewer.DescribeOpenView() != "nothing open") await PressKey(Key.Escape);

        // (b2) Regression (user report): move the game's D binding to another key (as the game's keybinding
        // screen does) — D must then count as free: saved without a game-key warning, and it fires. D is still
        // bound in the *inactive* keyboard-only map on this profile, which used to wrongly block it.
        // Edits the in-memory map only (the game's ModifyMKbKey would also save the player's keybindings).
        var mkb = (Dictionary<StringName, Key>)Traverse.Create(NInputManager.Instance!).Field("_mKbInputMap").GetValue();
        var movedOffD = mkb.Where(kv => kv.Value == Key.D).Select(kv => kv.Key).ToList();
        foreach (StringName action in movedOffD) mkb[action] = Key.P;
        Log($"hotkeys: moved game actions off D → P: {string.Join(", ", movedOffD)}; native(D)={Hotkeys.NativeAction(Key.D) ?? "none"}");
        SettingsMenu.Open();
        SettingsMenu.GetHotkeyButton(0)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressKey(Key.D, shift: true);
        Log($"hotkeys: draw→Shift+D: status='{SettingsMenu.HotkeyStatus}' active draw={Hotkeys.Active[0, 0]}");
        SettingsMenu.Close();
        await PressKey(Key.D, shift: true);
        Log($"hotkeys: Shift+D → {PileViewer.DescribeOpenView()}");
        await PressKey(Key.D, shift: true);
        foreach (StringName action in movedOffD) mkb[action] = Key.D;
        Log($"hotkeys: restored game D binding; native(D)={Hotkeys.NativeAction(Key.D) ?? "none"}");

        // (c) Duplicate with a teammate slot that doesn't exist this game → saved with a warning; lowest teammate wins.
        SettingsMenu.Open();
        SettingsMenu.GetHotkeyButton(0)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressKey(Key.Z, ctrl: true); // teammate 2's default draw binding
        Log($"hotkeys: draw→Ctrl+Z ({teammates.Count} teammate(s)): status='{SettingsMenu.HotkeyStatus}' active draw={Hotkeys.Active[0, 0]}");
        SettingsMenu.Close();
        await PressKey(Key.Z, ctrl: true);
        Log($"hotkeys: Ctrl+Z → {PileViewer.DescribeOpenView()}");
        await PressKey(Key.Z, ctrl: true);

        SettingsMenu.Open(); // leave open for the screenshot
    }

    private static async Task PressKey(Key key, bool shift = false, bool ctrl = false)
    {
        // Inject through Godot's input pipeline (same route as a physical key press), so this also checks
        // that the NGame._Input patch sees the key and nothing upstream swallows it.
        var down = new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = true };
        var up = new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = false };
        Input.ParseInputEvent(down);
        await Wait(0.1);
        Input.ParseInputEvent(up);
        await Wait(0.9);
    }

    /// <summary>
    /// Hand Viewer suite. Milestone 1: probe — log the native hand's node structure (layout, holders, the
    /// Hand Card Count index labels, the peek "eye" button) and every teammate's hand, as groundwork for the UI.
    /// </summary>
    private static async Task ExerciseHands(bool mutator, bool observer)
    {
        Settings.ResetForAutoTest();
        for (int i = 0; i < 120 && !CombatManager.Instance.IsInProgress; i++) await Wait(0.25);
        Log($"hands: combat in progress: {CombatManager.Instance.IsInProgress} (mutator={mutator}, observer={observer})");
        await Wait(3.0);

        NPlayerHand? hand = NPlayerHand.Instance;
        if (hand == null)
        {
            Log("hands: no NPlayerHand");
        }
        else
        {
            Log($"hands: NPlayerHand pos={hand.Position} global={hand.GlobalPosition} size={hand.Size} holders={hand.ActiveHolders.Count} " +
                $"showCardIndices={SaveManager.Instance.PrefsSave.ShowCardIndices}");
            if (hand.ActiveHolders.FirstOrDefault() is NHandCardHolder holder)
            {
                Log("hands: first holder tree:\n" + PileViewer.DescribeTree(holder, 0));
            }
            Log("hands: peek button tree:\n" + PileViewer.DescribeTree(hand.PeekButton, 0));
            Log("hands: holder transforms: " + string.Join("; ", hand.ActiveHolders.Select((h, i) =>
                $"#{i} pos={h.Position} rot={h.RotationDegrees:0.#} scale={h.Scale.X:0.##} card={h.CardNode?.Model?.Id.Entry}")));
        }

        foreach (var (number, player) in PileViewer.Teammates())
        {
            var cards = PileType.Hand.GetPile(player).Cards;
            Log($"hands: P{number} {player.Character.Id.Entry} hand ({cards.Count}): " +
                string.Join(", ", cards.Select(c => $"{c.Title}[{c.EnergyCost.GetWithModifiers(CostModifiers.All)}]")));
        }
        Log("done");
    }

    private static async Task WatchCounts(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            Log($"watch t+{i}s: {PileViewer.DescribeState()}");
            await Wait(1.0);
        }
    }

    private static EncounterModel PickEncounter() =>
        ModelDb.AllEncounters.First(e => e.RoomType == RoomType.Monster && e.IsWeak);

    private static async Task Wait(double seconds)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void Log(string message) => ModLog.Info("AutoTest: " + message);
}
