using System;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.GameActions;
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

        await ExerciseHandViewerUi();
        SettingsMenu.Close();
        await ExerciseLiveHand(mutator, observer);
        Log("done");
    }

    /// <summary>
    /// hv3: the observer (host) views the mutator's (client 1000's) hand; the mutator draws, adds and removes hand
    /// cards through networked console actions; the observer logs its hand readout every second.
    /// </summary>
    private static async Task ExerciseLiveHand(bool mutator, bool observer)
    {
        const ulong mutatorId = 1000;
        if (observer) HandViewer.View(mutatorId);
        Task watch = observer ? WatchHand(9) : Task.CompletedTask;
        if (mutator)
        {
            await Wait(1.0);
            RunState runState = RunManager.Instance.DebugOnlyGetState()!;
            Player me = LocalContext.GetMe(runState)!;
            string cardId = PileType.Hand.GetPile(me).Cards.First().Id.Entry;
            foreach (string cmd in new[] { "draw 2", $"card {cardId}", $"remove_card {cardId}" })
            {
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: true));
                Log($"hv3: mutator enqueued '{cmd}'");
                await Wait(2.0);
            }
            Log("hv3: mutator's own hand: " + HandViewer.DescribeHand(me));
        }
        await watch;
        if (observer) await ExerciseCloseups();
        await ExerciseAccuracy(mutator, observer);
        if (observer) await ExerciseHideHand();
        await ExerciseThirdPlayer(observer);
    }

    /// <summary>
    /// hv8 (3+ players): the host switches its view from client 1000 to client 1001 with 1001's eye and back; each
    /// time the rendered hand must equal that client's own hand. Client 1001 logs its own hand as the reference.
    /// </summary>
    private static async Task ExerciseThirdPlayer(bool observer)
    {
        if (ExpectedPlayers < 3) return;
        if (ClientIndex == 1)
        {
            await Wait(4.0);
            foreach (string line in HandViewer.DumpOwnHand()) Log("hv8: ref1001 " + line);
            Log("hv8: ref1001 done");
            return;
        }
        if (!observer) return;
        foreach (ulong id in new ulong[] { 1001, 1000 })
        {
            PileViewer.SimulatePress(PileViewer.StripIndexOf(id), PileType.Hand);
            await Wait(2.0);
            Log($"hv8: switched → {HandViewer.DescribeState()}");
            foreach (string line in HandViewer.DumpTeammateCards()) Log($"hv8: view{id} " + line);
        }
    }

    /// <summary>hv7: Hide hand turns your own cards into dark silhouettes with a white outline; Show hand restores them.</summary>
    private static async Task ExerciseHideHand()
    {
        Log("hv7: before: " + HandViewer.DescribeOwnHandLook());
        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(1.0);
        Log("hv7: hidden: " + HandViewer.DescribeOwnHandLook() + " | " + HandViewer.DescribeState());
        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(1.0);
        Log("hv7: shown again: " + HandViewer.DescribeOwnHandLook());

        // Draggable button (real mouse events): a drag moves it without toggling; a click toggles; F1 reset restores.
        if (HandViewer.HideButton is Button button)
        {
            Log("hv7: button default (above energy): " + HandViewer.DescribeState());
            Vector2 start = button.GetGlobalRect().GetCenter();
            await Mouse(start, pressed: true);
            await Mouse(start + new Vector2(30f, -20f), pressed: null);
            await Mouse(start + new Vector2(80f, -50f), pressed: null);
            await Mouse(start + new Vector2(80f, -50f), pressed: false);
            Log($"hv7: after drag: offset={Settings.HideButtonOffset} {HandViewer.DescribeState()}");
            Vector2 now = button.GetGlobalRect().GetCenter();
            await Mouse(now, pressed: true);
            await Mouse(now, pressed: false);
            Log("hv7: after click: " + HandViewer.DescribeState());
            SettingsMenu.Open();
            SettingsMenu.ResetPositionButton!.EmitSignal(BaseButton.SignalName.Pressed);
            SettingsMenu.Close();
            await Wait(0.5);
            Log($"hv7: after F1 reset: offset={Settings.HideButtonOffset} {HandViewer.DescribeState()}");
        }
        if (!HandViewer.HideOwnHand) HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed); // leave hidden for the screenshot
        await Wait(1.0);
    }

    /// <summary>Real mouse event at a viewport position (converted to window pixels): press/release, or motion if null.</summary>
    private static async Task Mouse(Vector2 viewportPos, bool? pressed)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 point = viewportPos * ((Vector2)DisplayServer.WindowGetSize() / tree.Root.GetVisibleRect().Size);
        InputEvent ev = pressed is bool p
            ? new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = p, Position = point, GlobalPosition = point,
                ButtonMask = p ? MouseButtonMask.Left : 0 }
            : new InputEventMouseMotion { Position = point, GlobalPosition = point, ButtonMask = MouseButtonMask.Left };
        Input.ParseInputEvent(ev);
        await Wait(0.2);
    }

    /// <summary>
    /// hv6: the mutator applies effects that change card numbers and costs (Strength, Dexterity, Weak, Frail,
    /// Snecko Eye + draws, Spiked Gauntlets, a Sharp enchant, playing Enlightenment), all through networked actions.
    /// Then both sides log every hand card's rendered cost and description: the mutator from the game's own hand,
    /// the observer from the Hand Viewer. The two lists must match line for line.
    /// </summary>
    private static async Task ExerciseAccuracy(bool mutator, bool observer)
    {
        await Wait(1.0);
        string Power<T>() => ModelDb.AllPowers.First(p => p is T).Id.Entry;
        string Relic<T>() => ModelDb.AllRelics.First(r => r is T).Id.Entry;
        string enlightenment = ModelDb.AllCards.First(c => c is Enlightenment).Id.Entry;

        if (mutator)
        {
            CombatState combat = CombatManager.Instance.DebugOnlyGetState()!;
            Player me = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()!)!;
            int myIndex = combat.Creatures.ToList().IndexOf(me.Creature);

            await Enqueue(me, $"power {Power<StrengthPower>()} 3 {myIndex}", $"power {Power<DexterityPower>()} 2 {myIndex}");
            await Wait(2.0);
            DumpReference("A (Strength 3, Dexterity 2)");
            await Wait(5.0);

            await Enqueue(me, $"power {Power<WeakPower>()} 2 {myIndex}", $"power {Power<FrailPower>()} 2 {myIndex}");
            await Wait(2.0);
            DumpReference("B (+ Weak 2, Frail 2)");
            await Wait(5.0);

            // Make room, then cost changers: Snecko Eye randomizes the cost of drawn cards; Sharp on an attack.
            var hand = PileType.Hand.GetPile(me).Cards.ToList();
            await Enqueue(me, $"remove_card {hand[^1].Id.Entry}", $"remove_card {hand[^2].Id.Entry}",
                $"remove_card {hand[^3].Id.Entry}", $"remove_card {hand[^4].Id.Entry}",
                $"relic {Relic<SneckoEye>()}", $"relic {Relic<SpikedGauntlets>()}", "draw 3");
            int strikeIndex = PileType.Hand.GetPile(me).Cards.ToList().FindIndex(c => c.Type == CardType.Attack);
            await Enqueue(me, $"enchant {ModelDb.Enchantment<Sharp>().Id.Entry} 2 {Math.Max(strikeIndex, 0)}");
            await Wait(2.0);
            DumpReference("C (+ Snecko Eye draws, Spiked Gauntlets, Sharp)");
            await Wait(5.0);

            // Enlightenment: cards in hand cost at most 1 this turn.
            await Enqueue(me, $"card {enlightenment}");
            await Wait(1.0);
            if (PileType.Hand.GetPile(me).Cards.FirstOrDefault(c => c is Enlightenment) is CardModel played)
            {
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(played, null));
                Log("hv6: mutator played Enlightenment");
            }
            await Wait(2.5);
            DumpReference("D (+ played Enlightenment)");
        }

        if (observer)
        {
            Player? teammate = PileViewer.Teammates().Select(t => t.player).FirstOrDefault(p => p.NetId == 1000);
            if (teammate == null) return;
            bool Has<T>() => teammate.Creature.Powers.Any(p => p is T);
            await WaitUntil(() => Has<StrengthPower>() && Has<DexterityPower>(), 20);
            await Wait(1.5);
            DumpViewer("A");
            await WaitUntil(() => Has<WeakPower>() && Has<FrailPower>(), 20);
            await Wait(1.5);
            DumpViewer("B");
            await WaitUntil(() => teammate.Relics.Any(r => r is SneckoEye)
                && PileType.Hand.GetPile(teammate).Cards.Any(c => c.Enchantment is Sharp), 30);
            await Wait(1.5);
            DumpViewer("C");
            await WaitUntil(() => !PileType.Hand.GetPile(teammate).Cards.Any(c => c is Enlightenment)
                && SlotPilesHave(teammate, c => c is Enlightenment), 30);
            await Wait(2.0);
            DumpViewer("D");
        }
    }

    private static bool SlotPilesHave(Player player, Func<CardModel, bool> match) =>
        PileViewer.SlotPiles.Any(p => p.GetPile(player).Cards.Any(match));

    private static async Task Enqueue(Player me, params string[] commands)
    {
        foreach (string cmd in commands)
        {
            RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: true));
            Log($"hv6: mutator enqueued '{cmd}'");
            await Wait(0.6);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, double timeoutSeconds)
    {
        for (double t = 0; t < timeoutSeconds && !condition(); t += 0.25) await Wait(0.25);
    }

    private static void DumpReference(string phase)
    {
        var mine = HandViewer.DumpOwnHand();
        Log($"hv6: REFERENCE {phase} ({mine.Count})");
        foreach (string line in mine) Log($"hv6: ref{phase[0]} {line}");
    }

    private static void DumpViewer(string phase)
    {
        var seen = HandViewer.DumpTeammateCards();
        Log($"hv6: VIEWER {phase} ({seen.Count})");
        foreach (string line in seen) Log($"hv6: view{phase[0]} {line}");
    }

    /// <summary>hv5: point the mouse at teammate cards (through the real input pipeline) and check the native closeup.</summary>
    private static async Task ExerciseCloseups()
    {
        await Wait(1.0);
        foreach (int index in new[] { 3, 0, 5 })
        {
            if (HandViewer.CardCenter(index) is not Vector2 target) continue;
            // Viewport (1920x1080 design) → window pixels.
            var tree = (SceneTree)Engine.GetMainLoop();
            Viewport vp = tree.Root;
            Vector2 window = (Vector2)DisplayServer.WindowGetSize();
            Vector2 factor = window / vp.GetVisibleRect().Size;
            // Aim at the visible top strip of the card (the rest is behind your own hand).
            Vector2 point = new Vector2(target.X, target.Y - 120f) * factor;
            Input.ParseInputEvent(new InputEventMouseMotion { Position = point, GlobalPosition = point });
            await Wait(0.8);
            Log($"hv5: hover card #{index} → closeup {HandViewer.DescribeFocus()}");
        }
        Log("hv5: " + HandViewer.DescribeCards());
    }

    private static async Task WatchHand(int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            Log($"hv3: watch t+{i}s: {HandViewer.CurrentText}");
            if (i % 3 == 0) Log($"hv4: t+{i}s: {HandViewer.DescribeCards()} | my index labels y: {HandViewer.DescribeIndexLabels()}");
            await Wait(1.0);
        }
    }

    /// <summary>hv2: the strip's Hand (eye) slot, one-teammate-at-a-time toggling, the Hide hand button, the F1 tab.</summary>
    private static async Task ExerciseHandViewerUi()
    {
        Log("hv2: strips: " + PileViewer.DescribeState());
        Log("hv2: hand slots: " + PileViewer.DescribeHandSlots());
        Log("hv2: start: " + HandViewer.DescribeState());

        PileViewer.SimulatePress(0, PileType.Hand);
        await Wait(0.5);
        Log("hv2: eye on strip 0 → " + HandViewer.DescribeState());
        if (PileViewer.Teammates().Count >= 2)
        {
            PileViewer.SimulatePress(1, PileType.Hand);
            await Wait(0.5);
            Log("hv2: eye on strip 1 (should switch) → " + HandViewer.DescribeState());
            PileViewer.SimulatePress(1, PileType.Hand);
        }
        else
        {
            PileViewer.SimulatePress(0, PileType.Hand);
        }
        await Wait(0.5);
        Log("hv2: eye again (should close) → " + HandViewer.DescribeState());

        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.5);
        Log("hv2: hide-hand pressed → " + HandViewer.DescribeState());
        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.5);
        Log("hv2: hide-hand pressed again → " + HandViewer.DescribeState());

        SettingsMenu.Open();
        SettingsMenu.Tabs!.CurrentTab = 1;
        await Wait(0.5);
        SettingsMenu.HandVisibleSlider!.Value = 45;
        SettingsMenu.HandShadowSlider!.Value = 70;
        Log($"hv2: menu tab '{SettingsMenu.Tabs.GetTabTitle(SettingsMenu.Tabs.CurrentTab)}': visible={Settings.HandVisible} shadow={Settings.HandShadow}");
        PileViewer.SimulatePress(0, PileType.Hand);
        SettingsMenu.HandViewerToggle!.ButtonPressed = false;
        await Wait(0.5);
        Log($"hv2: Hand Viewer off → slots {PileViewer.DescribeHandSlots()}; {HandViewer.DescribeState()}");
        SettingsMenu.HandViewerToggle.ButtonPressed = true;
        await Wait(0.5);
        Log($"hv2: Hand Viewer on → slots {PileViewer.DescribeHandSlots()}; {HandViewer.DescribeState()}");
        SettingsMenu.HandVisibleSlider.Value = 25; // back to the defaults for the rest of the suite
        SettingsMenu.HandShadowSlider.Value = 50;
        PileViewer.SimulatePress(0, PileType.Hand); // leave a hand view on + menu open for the screenshot
        await Wait(0.5);
        Log("hv2: final → " + HandViewer.DescribeState());
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
