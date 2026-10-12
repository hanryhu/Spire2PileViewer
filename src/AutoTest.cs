using System;
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

    private static readonly bool Multiplayer = OS.GetCmdlineArgs().Contains("-fastmp");

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

            await ExerciseStrips(mutator: true, observer: true);
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
                for (int i = 0; i < 240 && lobby.Players.Count < 2; i++) await Wait(0.5);
                Log($"lobby players: {lobby.Players.Count}");
                await Wait(2.0); // let the client pick and ready first
            }

            CharacterModel character = ModelDb.AllCharacters.ElementAt(isHost ? 0 : 1);
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

            await ExerciseStrips(mutator: !isHost, observer: isHost);
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

        // --- Shift+drag moves all strips; the menu's reset button puts them back ---
        PileViewer.SimulateDrag(new Vector2(-40f, -60f));
        await Wait(0.5);
        Log($"after drag: offset={Settings.Offset} state: {PileViewer.DescribeState()}");
        SettingsMenu.ResetPositionButton!.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.5);
        Log($"after menu reset: offset={Settings.Offset} state: {PileViewer.DescribeState()}");
        Log($"settings file: scale={Settings.Scale} opacity={Settings.Opacity} enabled={Settings.Enabled}");
        Log("done — screenshot window now (menu left open)");
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
