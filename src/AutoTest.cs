using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>
/// Development-only smoke test, inert unless the game is launched with -pileviewerautotest
/// (which also implies -pileviewerself). From the main menu it starts an unsaved solo run, jumps
/// into a monster fight, then exercises the strip and logs what it finds, so the mod can be checked
/// from the log file (and a screenshot) without anyone clicking through the game.
/// </summary>
public static class AutoTest
{
    public static readonly bool Enabled = OS.GetCmdlineArgs().Contains("-pileviewerautotest");

    private static bool _started;

    [HarmonyPatch(typeof(NMainMenu), "_Ready")]
    public static class MainMenuReadyPatch
    {
        private static void Postfix()
        {
            if (!Enabled || _started) return;
            _started = true;
            TaskHelper.RunSafely(Run());
        }
    }

    private static async Task Run()
    {
        try
        {
            Log("starting");
            await Wait(2.0);
            CharacterModel character = ModelDb.AllCharacters.First();
            await NGame.Instance!.StartNewSingleplayerRun(character, shouldSave: false, ActModel.GetDefaultList().ToList(),
                Array.Empty<ModifierModel>(), SeedHelper.GetRandomSeed(), GameMode.Standard);
            Log($"run started as {character.Id.Entry}");
            await Wait(3.0);

            EncounterModel encounter = ModelDb.AllEncounters.First(e => e.RoomType == RoomType.Monster && e.IsWeak).ToMutable();
            encounter.DebugRandomizeRng();
            await RunManager.Instance.EnterRoomDebug(RoomType.Monster, MapPointType.Unassigned, encounter);
            Log($"entered encounter {encounter.Id.Entry}");

            for (int i = 0; i < 40 && !CombatManager.Instance.IsInProgress; i++) await Wait(0.25);
            Log($"combat in progress: {CombatManager.Instance.IsInProgress}");
            await Wait(3.0);

            Log("state: " + PileViewer.DescribeState());
            foreach (PileType pile in PileViewer.SlotPiles)
            {
                PileViewer.SimulatePress(pile);
                await Wait(1.0);
            }
            Log("done — screenshot window now");
        }
        catch (Exception ex)
        {
            ModLog.Error("AutoTest", ex);
        }
    }

    private static async Task Wait(double seconds)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void Log(string message) => ModLog.Info("AutoTest: " + message);
}
