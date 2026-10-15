using System;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models.Enchantments;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Encounters;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.PeerInput;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace Spire2PileViewer;

/// <summary>
/// Development-only multiplayer test, inert unless the game is launched with -pileviewerautotest and -fastmp
/// (scripts/dev-autotest-mp.ps1). In the lobby everyone picks a character and readies up, the host sends a
/// networked "fight", then the suite runs (see RunSuite). Every step is logged as "AutoTest: ...", checks as
/// "AutoTest: CHECK PASS|FAIL ...", ending in "AutoTest: RESULT ..." and "AutoTest: done".
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

        // Hover a teammate's strip so the shot also shows their lit frame in the player list.
        if (PileViewer.StripAndRow(PileViewer.Teammates()[0].player.NetId) is { } placed) await Mouse(placed.Strip.GetCenter(), pressed: null);
        await Wait(0.5);
        if (PileViewer.PlayerRow(PileViewer.Teammates()[0].player) is { } row) Log("player row tree:\n" + PileViewer.DescribeTree(row, 0));
        Shot(1);
        Log("SHOT 1");
        await Wait(6.0);
        await Mouse(new Vector2(960f, 300f), pressed: null);
        SettingsMenu.Open();
        await Wait(1.0);
        Shot(2);
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
        Shot(3);
        Log("SHOT 3 " + PileViewer.DescribeOpenView());
        await Wait(6.0);
        PileViewer.OpenTeammatePile(0, PileType.Discard); // close it
        await Wait(1.0);

        // Hand Viewer: the teammate's hand behind yours with one card raised, then your hand hidden with a closeup.
        HandViewer.View(PileViewer.Teammates()[0].player.NetId);
        await Wait(2.0);
        if (HandViewer.PeekPoint(HandViewer.MiddleIndexOf(_ => true)) is Vector2 peek) await Mouse(peek, pressed: null);
        await Wait(1.5);
        Shot(4);
        Log("SHOT 4 " + HandViewer.DescribeCloseup());
        await Wait(6.0);
        await Mouse(new Vector2(960f, 300f), pressed: null);
        HandViewer.ToggleHideOwnHand();
        await Wait(1.0);
        if (HandViewer.CardCenter(HandViewer.MiddleIndexOf(_ => true)) is Vector2 target) await Mouse(target, pressed: null);
        await Wait(1.5);
        Shot(5);
        Log("SHOT 5 " + HandViewer.DescribeFocus());
        await Wait(6.0);
        HandViewer.ToggleHideOwnHand();
        await Mouse(new Vector2(960f, 300f), pressed: null);
        await Wait(1.0);

        // Aim one of their attacks at the enemy: the game's arrow, numbers from their state.
        int attack = HandViewer.MiddleIndexOf(c => c.TargetType == TargetType.AnyEnemy);
        Creature? enemy = CombatManager.Instance.DebugOnlyGetState()?.HittableEnemies.FirstOrDefault();
        if (attack >= 0 && enemy != null && HandViewer.PeekPoint(attack) is Vector2 card)
        {
            Vector2 from = card;
            Vector2 to = NCombatRoom.Instance!.GetCreatureNode(enemy)!.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(from, pressed: null);
            await Wait(0.8);
            await Mouse(from, pressed: true);
            for (int i = 1; i <= 8; i++) await Mouse(from.Lerp(to, i / 8f), pressed: null);
            await Wait(1.0);
            Shot(6);
            Log("SHOT 6 " + HandViewer.DescribeDragCard());
            await Wait(6.0);
            await RightClick(to);
            await Mouse(to, pressed: false);
        }
        Log("done");
    }

    private static readonly bool Multiplayer = OS.GetCmdlineArgs().Contains("-fastmp");

    /// <summary>-pileviewerplayers N: party size the host waits for (default 2).</summary>
    private static readonly int ExpectedPlayers = ArgValue("-pileviewerplayers", 2);

    /// <summary>0 for the first client (-clientId 1000), 1 for the next, ...; -1 for the host.</summary>
    private static readonly int ClientIndex = ArgValue("-clientId", 999) - 1000;

    /// <summary>
    /// -autotestsuite fast|full|mixed: fast (default, ~30 s) checks every feature once; full (up to 3 min, before
    /// pushes) adds cross-instance accuracy, glow, regressions and a second fight; mixed is for a teammate
    /// without the mod (launch-mp.ps1 -VanillaSide).
    /// </summary>
    private static readonly string Suite = ArgString("-autotestsuite", "fast");

    /// <summary>-autotestskipfast (with full): leave out the fast checks (they ran separately).</summary>
    private static readonly bool SkipFastChecks = OS.GetCmdlineArgs().Contains("-autotestskipfast");

    /// <summary>-pileviewerharness: this instance runs only the test fixtures (no Pile Viewer patches).</summary>
    internal static readonly bool Harness = OS.GetCmdlineArgs().Contains("-pileviewerharness");

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

    /// <summary>Peer-input messages this game sent (the game's cursor/hover/targeting sync), for the network check.</summary>
    internal static int PeerInputSends;

    [HarmonyPatch(typeof(PeerInputSynchronizer), "SendSyncMessage")]
    public static class PeerInputSendPatch
    {
        private static void Postfix() => PeerInputSends++;
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

    private static async Task RunMultiplayer(StartRunLobby lobby)
    {
        try
        {
            bool isHost = lobby.NetService.Type == NetGameType.Host;
            Log($"multiplayer: in lobby as {(isHost ? "host" : "client")} (netId {lobby.NetService.NetId})");
            if (isHost)
            {
                await WaitUntil(() => lobby.Players.Count >= ExpectedPlayers, 120);
                Log($"lobby players: {lobby.Players.Count}");
            }
            await Wait(0.3);
            CharacterModel character = ModelDb.AllCharacters.ElementAt(isHost ? 0 : 1 + ClientIndex);
            lobby.SetLocalCharacter(character);
            await Wait(0.3);
            lobby.SetReady(ready: true);
            Log($"ready as {character.Id.Entry}");

            await WaitUntil(() => RunManager.Instance.DebugOnlyGetState() != null, 120);
            RunState? runState = RunManager.Instance.DebugOnlyGetState();
            Log($"run in progress: {runState != null} ({runState?.Players.Count ?? 0} players)");
            if (runState == null) return;
            await WaitUntil(() => LocalContext.GetMe(runState) != null, 30);
            await Wait(0.5);

            if (isHost && Suite != "mixed") // mixed: the fight is reached by playing (no debug commands with a vanilla peer)
            {
                Player me = LocalContext.GetMe(runState) ?? throw new InvalidOperationException("no local player");
                string cmd = "fight " + PickEncounter().Id.Entry;
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat: false));
                Log($"host enqueued '{cmd}'");
            }

            if (Showcase) await RunShowcase(isHost);
            else if (Suite == "mixed") await ExerciseMixed(isHost);
            else await RunSuite(isHost);
        }
        catch (Exception ex)
        {
            ModLog.Error("AutoTest", ex);
            Log("done"); // let the runner finish instead of waiting for the timeout
        }
    }

    /// <summary>
    /// Mixed install (launch-mp.ps1 -VanillaSide host|client): the teammate runs the unmodded game. Once a fight
    /// is reached by playing, the modded instance uses every feature on the teammate: strips,
    /// a pile screen, their hand, a closeup and an aim preview. Nothing here sends anything the vanilla game
    /// doesn't know; check both instances' godot logs for handshake warnings and state divergence.
    /// </summary>
    private static async Task ExerciseMixed(bool isHost)
    {
        // The fight is reached by playing (Neow, then a map node) — no debug commands with a vanilla peer.
        await WaitUntil(() => CombatManager.Instance.IsInProgress, 600);
        Log($"mixed: combat in progress: {CombatManager.Instance.IsInProgress}");
        if (!CombatManager.Instance.IsInProgress) { Log("done"); return; }
        Settings.ResetForAutoTest();
        await Wait(3.0);
        Log("mixed: strips " + PileViewer.DescribeState());
        Log($"mixed: default view = {HandViewer.ViewedNetId?.ToString() ?? "nobody"}; cards: {HandViewer.DescribeCards()}");
        foreach (string line in HandViewer.DumpTeammateCards()) Log("mixed: view " + line);
        Log("mixed: glow " + HandViewer.DescribeTeammateGlow());

        PileViewer.OpenTeammatePile(0, PileType.Draw);
        await Wait(1.5);
        Log("mixed: pile " + PileViewer.DescribeOpenView());
        PileViewer.OpenTeammatePile(0, PileType.Draw);
        await Wait(1.0);

        int attack = HandViewer.MiddleIndexOf(c => c.TargetType == TargetType.AnyEnemy);
        Creature? enemy = CombatManager.Instance.DebugOnlyGetState()?.HittableEnemies.FirstOrDefault();
        if (attack >= 0 && enemy != null && HandViewer.CardCenter(attack) is Vector2 card)
        {
            Vector2 from = card - new Vector2(0f, 155f);
            Vector2 to = NCombatRoom.Instance!.GetCreatureNode(enemy)!.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(from, pressed: null);
            await Wait(1.0);
            Log("mixed: hover " + HandViewer.DescribeHover());
            await Mouse(from, pressed: true);
            for (int i = 1; i <= 8; i++) await Mouse(from.Lerp(to, i / 8f), pressed: null);
            await Wait(1.0);
            Log($"mixed: aiming={HandViewer.IsAiming} {HandViewer.DescribeDragCard()}");
            await Mouse(to, pressed: false);
            await Wait(1.0);
            Log($"mixed: released, dragging={HandViewer.IsDragging}");
        }
        await PressKey(Key.H);
        await Wait(0.5);
        Log($"mixed: H → hideOwnHand={HandViewer.HideOwnHand}");
        await PressKey(Key.H);
        await Wait(5.0);
        Log($"mixed: still in combat after everything: {CombatManager.Instance.IsInProgress}");
        Log("done");
    }

    /// <summary>Showcase: the game saves its own frame (no window capture, so window focus doesn't matter).</summary>
    private static void Shot(int n)
    {
        string dir = System.IO.Path.Combine(OS.GetUserDataDir(), "showcase");
        System.IO.Directory.CreateDirectory(dir);
        Image image = ((SceneTree)Engine.GetMainLoop()).Root.GetTexture().GetImage();
        image.SavePng(System.IO.Path.Combine(dir, $"shot_{n}.png"));
        Log($"saved shot {n} ({image.GetWidth()}x{image.GetHeight()})");
    }

    private static async Task RightClick(Vector2 viewportPos)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 point = viewportPos * ((Vector2)DisplayServer.WindowGetSize() / tree.Root.GetVisibleRect().Size);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true, Position = point, GlobalPosition = point, ButtonMask = MouseButtonMask.Right });
        await Wait(0.1);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false, Position = point, GlobalPosition = point });
        await Wait(0.1);
    }

    private static async Task MouseButtonPress(MouseButton button)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        Vector2 point = new Vector2(960f, 300f) * ((Vector2)DisplayServer.WindowGetSize() / tree.Root.GetVisibleRect().Size);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = point, GlobalPosition = point });
        await Wait(0.1);
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = point, GlobalPosition = point });
        await Wait(0.2);
    }

    // =====================================================================================================
    // Test suites. "fast" (default, ~30 s with launch): every feature once, in-process checks on the host.
    // "full" (≤ 3 min, before pushes): fast + cross-instance accuracy/glow (lock-step with the first client),
    // hotkey regressions, hover delay ends, 3rd player, whose hand carries over to the next fight.
    // Each check logs "CHECK PASS|FAIL name — detail"; the runner (dev-autotest-mp.ps1) prints them, compares
    // the REFn (client) / VIEWn (host) dumps, and ends with a summary. Roles: host = observer, first client
    // (1000) = mutator, second client (1001, full with 3 players) = a static reference hand.
    // =====================================================================================================

    private static bool Full => Suite == "full";

    private const ulong MutatorId = 1000;

    private static int _pass;

    private static int _fail;

    private static bool Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log($"CHECK {(ok ? "PASS" : "FAIL")} {name}{(detail == "" ? "" : " — " + detail)}");
        return ok;
    }

    private static Player Me => LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()!)!;

    private static CombatState Combat => CombatManager.Instance.DebugOnlyGetState()!;

    private static void Send(Player me, string cmd, bool inCombat = true)
    {
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd, inCombat));
        Log($"sent '{cmd}'");
    }

    private static Creature FirstEnemy => Combat.HittableEnemies.First();

    /// <summary>Hotkey row of the mutator (teammate number - 1; party order follows join order, not launch order).</summary>
    private static int MutatorRow => PileViewer.Teammates().FindIndex(t => t.player.NetId == MutatorId);

    /// <summary>Default modifiers of a hotkey row: Shift, Ctrl, Ctrl+Shift.</summary>
    private static (bool Shift, bool Ctrl) RowMods(int row) => row switch { 0 => (true, false), 1 => (false, true), _ => (true, true) };

    private static string RowKey(int row, string key) => (RowMods(row).Ctrl ? "Ctrl+" : "") + (RowMods(row).Shift ? "Shift+" : "") + key;

    private static Task PressRowKey(int row, Key key) => PressKey(key, RowMods(row).Shift, RowMods(row).Ctrl);

    private static void SelectHotkeyRow(int row)
    {
        SettingsMenu.TeammatePicker!.Select(row);
        SettingsMenu.TeammatePicker.EmitSignal(OptionButton.SignalName.ItemSelected, (long)row);
    }

    private static int CreatureIndex(Creature c) => Combat.Creatures.ToList().IndexOf(c);

    private static string Id<T>(IEnumerable<AbstractModel> models) => models.First(m => m is T).Id.Entry;

    private static async Task RunSuite(bool isHost)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Settings.ResetForAutoTest();
        await WaitUntil(() => CombatManager.Instance.IsInProgress && NPlayerHand.Instance != null, 60);
        if (!Check("combat started", CombatManager.Instance.IsInProgress && NPlayerHand.Instance != null))
        {
            Log($"RESULT suite={Suite} pass={_pass} fail={_fail} time={clock.Elapsed.TotalSeconds:0.0}s window={DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}");
            Log("done");
            return;
        }
        // Wait for the opening hand to be dealt and settle.
        await WaitUntil(() => NPlayerHand.Instance!.ActiveHolders.Count > 0 && NPlayerHand.Instance.ActiveHolders.Count == PileType.Hand.GetPile(Me).Cards.Count, 15);
        await Wait(0.3);
        Log($"suite {Suite}: combat ready after {clock.Elapsed.TotalSeconds:0.0}s in this phase");
        try
        {
            if (Suite == "explore")
            {
                if (isHost) await ExploreHost();
                else await ExploreTeammate();
            }
            else if (Suite == "harness")
            {
                if (isHost) await HarnessObserver();
                else if (Me.NetId == MutatorId) await HarnessTeammate();
                else await HarnessBystander();
            }
            else if (isHost) await Observer();
            else if (Me.NetId == MutatorId) await Mutator();
            else if (Full) await StaticReference();
        }
        catch (Exception ex)
        {
            Check("no exception", false, ex.ToString().Replace('\n', ' '));
        }
        Log($"RESULT suite={Suite} pass={_pass} fail={_fail} time={clock.Elapsed.TotalSeconds:0.0}s window={DisplayServer.WindowGetSize().X}x{DisplayServer.WindowGetSize().Y}");
        Log("done");
    }

    // ---------------- Lock-step (full) ----------------

    private static Player Host => Combat.Players.First(p => p.NetId == 1);

    private static Player MutatorPlayer => Combat.Players.First(p => p.NetId == MutatorId);

    private static int _hostGold0;

    private static int _mutatorGold0;

    /// <summary>Observer: ask the mutator for step k (host gold +1) and wait until it's done (mutator gold +1).</summary>
    private static async Task<bool> Ask(int k)
    {
        Send(Me, "gold 1");
        await WaitUntil(() => MutatorPlayer.Gold >= _mutatorGold0 + k, 30);
        return Check($"mutator finished step {k}", MutatorPlayer.Gold >= _mutatorGold0 + k);
    }

    private const int MutatorSteps = 9;

    // Mutator, steps 6-7: what the host showed over their head while we watched.
    private static bool _watchHostHover;

    private static readonly List<string> _hostHoverSeen = new();

    private static async Task WatchHostHover()
    {
        while (_watchHostHover)
        {
            if (RunManager.Instance.HoveredModelTracker.GetHoveredModel(Host.NetId) is AbstractModel m) _hostHoverSeen.Add(m.Id.Entry);
            await Wait(0.03);
        }
    }

    private static async Task Mutator()
    {
        Player me = Me;
        _hostGold0 = Host.Gold;
        _mutatorGold0 = me.Gold;
        // Pile changes for the host's live-update check.
        var opening = PileType.Hand.GetPile(me).Cards;
        string first = (opening.FirstOrDefault(c => !c.Id.Entry.StartsWith("STRIKE")) ?? opening.First()).Id.Entry;
        Send(me, "draw 1");
        Send(me, $"card {first} Discard");
        Send(me, $"card {first} Exhaust");
        // Max HP 70 → 114 (Big Mushroom +20, Mango +14, Pear +10): a longer health bar in the player list.
        Send(me, $"relic {Id<BigMushroom>(ModelDb.AllRelics)}");
        Send(me, $"relic {Id<Mango>(ModelDb.AllRelics)}");
        Send(me, $"relic {Id<Pear>(ModelDb.AllRelics)}");
        if (!Full) return;

        for (int k = 1; k <= MutatorSteps; k++)
        {
            await WaitUntil(() => Host.Gold >= _hostGold0 + k, 120);
            if (Host.Gold < _hostGold0 + k)
            {
                Check($"host asked for step {k}", false);
                return;
            }
            await MutatorStep(k, me);
            int gold = me.Gold;
            Send(me, "gold 1");
            await WaitUntil(() => me.Gold > gold, 10);
        }
    }

    private static async Task MutatorStep(int k, Player me)
    {
        var hand = PileType.Hand.GetPile(me);
        switch (k)
        {
            case 1:
                // Cost/number changers (the host already put Strength + Weak on us and Vulnerable on the enemy),
                // then one hit on the enemy so Beat Into Shape has hits to count.
                int attack = hand.Cards.ToList().FindIndex(c => c.Type == CardType.Attack);
                Send(me, $"relic {Id<SpikedGauntlets>(ModelDb.AllRelics)}");
                Send(me, $"enchant {ModelDb.Enchantment<Sharp>().Id.Entry} 2 {Math.Max(attack, 0)}");
                Send(me, $"card {Id<BeatIntoShape>(ModelDb.AllCards)}");
                Send(me, $"card {Id<EchoingSlash>(ModelDb.AllCards)}");
                Send(me, "energy 6");
                await WaitUntil(() => hand.Cards.Any(c => c is EchoingSlash) && me.Relics.Any(r => r is SpikedGauntlets), 10);
                if (hand.Cards.FirstOrDefault(c => c.Id.Entry.StartsWith("STRIKE") && c.CanPlay()) is CardModel strike)
                {
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(strike, FirstEnemy));
                    await WaitUntil(() => !hand.Cards.Contains(strike), 10);
                }
                Send(me, $"card {Id<Lift>(ModelDb.AllCards)}"); // an ally-targeting card for the host to aim
                await WaitUntil(() => hand.Cards.Any(c => c is Lift), 10);
                await WaitUntil(() => NPlayerHand.Instance!.ActiveHolders.Count == hand.Cards.Count, 10);
                await Wait(0.8);
                foreach (string line in HandViewer.DumpOwnHand()) Log("REF1 " + line);
                break;
            case 2:
                NCombatRoom.Instance!.Ui.EndTurnButton.CallReleaseLogic();
                await WaitUntil(() => CombatManager.Instance.IsPlayerReadyToEndTurn(me), 10);
                break;
            case 3:
                await WaitUntil(() => Traverse.Create(NCombatRoom.Instance!.Ui.EndTurnButton).Property<bool>("CanTurnBeEnded").Value, 5);
                NCombatRoom.Instance!.Ui.EndTurnButton.CallReleaseLogic();
                await WaitUntil(() => !CombatManager.Instance.IsPlayerReadyToEndTurn(me), 10);
                break;
            case 4:
                // Spend all energy on Strikes/Defends (no choice prompts).
                for (int i = 0; i < 10 && me.PlayerCombatState!.Energy > 0; i++)
                {
                    var card = hand.Cards.Where(c => c.CanPlay() && c.EnergyCost.GetWithModifiers(CostModifiers.All) > 0
                            && (c.Id.Entry.StartsWith("STRIKE") || c.Id.Entry.StartsWith("DEFEND")))
                        .OrderByDescending(c => c.EnergyCost.GetWithModifiers(CostModifiers.All)).FirstOrDefault();
                    if (card == null) break;
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card,
                        card.TargetType == TargetType.AnyEnemy ? FirstEnemy : null));
                    await WaitUntil(() => !hand.Cards.Contains(card), 10);
                }
                await WaitUntil(() => NPlayerHand.Instance!.ActiveHolders.Count == hand.Cards.Count, 10);
                await Wait(1.0); // glow tweens
                Log($"REF2 energy={me.PlayerCombatState!.Energy} {HandViewer.DescribeOwnGlow()}");
                break;
            case 5:
            {
                // The host is aiming our Beat Into Shape from its Hand Viewer: we see it over the host's head,
                // glowing (it's ours); then we play it out from under the host's aim.
                CardModel? tried = hand.Cards.FirstOrDefault(c => c is BeatIntoShape);
                var tracker = RunManager.Instance.HoveredModelTracker;
                await WaitUntil(() => tried != null && tracker.GetHoveredModel(Host.NetId) == tried, 5);
                Check("you see the card the host is trying out, over their head", tried != null && tracker.GetHoveredModel(Host.NetId) == tried,
                    $"{tracker.GetHoveredModel(Host.NetId)?.Id.Entry ?? "nothing"}");
                await WaitUntil(() => HandViewer.SignalGlowing(Host.NetId), 2);
                var widths = new List<float>();
                for (int i = 0; i < 24; i++)
                {
                    widths.Add(HandViewer.SignalGlowWidthNow(Host.NetId));
                    await Wait(0.05);
                }
                Check("...blinking gold about once a second: it's yours", HandViewer.SignalGlowing(Host.NetId) && widths.Min() < 0.02f && widths.Max() > 0.06f,
                    $"glow width {widths.Min():0.###}..{widths.Max():0.###} over 1.2 s");
                bool wasGlowing = HandViewer.SignalGlowing(Host.NetId);
                Settings.CardSignals = Settings.CardSignalMode.Off;
                await Wait(0.2);
                Check("card signals Off: no gold on your card over their head", wasGlowing && !HandViewer.SignalGlowing(Host.NetId), $"glowing before: {wasGlowing}");
                Settings.CardSignals = Settings.CardSignalMode.SendAndReceive;
                if (tried == null) break;
                Send(me, "energy 3");
                await WaitUntil(() => me.PlayerCombatState!.Energy >= 1, 5);
                RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(tried, FirstEnemy));
                await WaitUntil(() => !hand.Cards.Contains(tried), 10);
                await WaitUntil(() => tracker.GetHoveredModel(Host.NetId) != tried, 5);
                Check("...and gone once you play it", tracker.GetHoveredModel(Host.NetId) != tried && !HandViewer.SignalGlowing(Host.NetId));
                break;
            }
            case 6:
                // Start watching what's over the host's head (they hover one of our cards within its hover delay).
                _hostHoverSeen.Clear();
                _watchHostHover = true;
                _ = TaskHelper.RunSafely(WatchHostHover());
                break;
            case 7:
                _watchHostHover = false;
                Check("nothing over the host's head while their hover delay runs", _hostHoverSeen.Count == 0,
                    _hostHoverSeen.Count == 0 ? "nothing seen" : string.Join(",", _hostHoverSeen.Distinct()));
                break;
            case 8:
            {
                var tracker = RunManager.Instance.HoveredModelTracker;
                await WaitUntil(() => tracker.GetHoveredModel(Host.NetId) is CardModel c && c.Owner == me, 3);
                Check("once their closeup shows, you see your card over the host's head",
                    tracker.GetHoveredModel(Host.NetId) is CardModel shown && shown.Owner == me, $"{tracker.GetHoveredModel(Host.NetId)?.Id.Entry ?? "nothing"}");
                await Wait(0.3);
                Check("...they only hover it: no gold", !HandViewer.SignalGlowing(Host.NetId));
                break;
            }
            case 9:
            {
                // The host presses and holds one of our cards that doesn't aim (no drag): it blinks gold.
                var tracker = RunManager.Instance.HoveredModelTracker;
                await WaitUntil(() => tracker.GetHoveredModel(Host.NetId) is CardModel c && c.Owner == me && HandViewer.SignalGlowing(Host.NetId), 3);
                Check("the host holds one of your cards that doesn't aim: it blinks gold",
                    tracker.GetHoveredModel(Host.NetId) is CardModel heldCard && heldCard.Owner == me
                    && heldCard.TargetType is not (TargetType.AnyEnemy or TargetType.AnyAlly) && HandViewer.SignalGlowing(Host.NetId),
                    $"{tracker.GetHoveredModel(Host.NetId)?.Id.Entry ?? "nothing"}, gold {HandViewer.SignalGlowing(Host.NetId)}");
                break;
            }
        }
    }

    /// <summary>Second client (full, 3 players): its own hand as the reference for the host's 3rd-player view.</summary>
    private static async Task StaticReference()
    {
        await Wait(1.0);
        foreach (string line in HandViewer.DumpOwnHand()) Log("REF3 " + line);
    }

    // ---------------- Observer (host) ----------------

    private static async Task Observer()
    {
        Player me = Me;
        var teammates = PileViewer.Teammates();
        Player mutator = teammates.First(t => t.player.NetId == MutatorId).player;
        _hostGold0 = me.Gold;

        // ---- Settings files from other versions (read without touching the real file) ----
        var problems = new List<string>();
        var old = Settings.ParseLenient("{\"enabled\":false,\"scale\":1.5,\"stripOffsets\":{\"1000\":[1,2]}}", problems);
        Check("settings from an old version load (missing keys keep their defaults)", old != null && problems.Count == 0 && !old.enabled
            && old.scale == 1.5f && old.schemaVersion == 0 && old.maxHandViewers == 1 && old.cardSignals == "both", string.Join("; ", problems));
        problems.Clear();
        var odd = Settings.ParseLenient("{\"schemaVersion\":99,\"scale\":\"big\",\"maxHandViewers\":[3],\"opacity\":0.5,\"handViewer\":null,\"future\":{\"x\":1}}", problems);
        Check("bad values and a newer version's file: bad ones reset, the rest kept", odd != null && odd.schemaVersion == 99 && odd.scale == 1f
            && odd.maxHandViewers == 1 && odd.opacity == 0.5f && odd.handViewer && problems.Count == 3, string.Join("; ", problems));
        problems.Clear();
        Check("a file that isn't JSON is rejected without a crash", Settings.ParseLenient("{not json", problems) == null && problems.Count == 1,
            string.Join("; ", problems));
        _mutatorGold0 = mutator.Gold;
        Vector2 away = new(960f, 300f);
        if (SkipFastChecks)
        {
            // Full checks only: they still need the Vulnerable the fast checks' aim preview puts on the enemy.
            Send(me, $"power {Id<VulnerablePower>(ModelDb.AllPowers)} 2 {CreatureIndex(FirstEnemy)}");
            await WaitUntil(() => FirstEnemy.Powers.Any(p => p is VulnerablePower), 5);
        }
        else
        {
            await ObserverFast(me, mutator, teammates, away);
        }
        if (Full) await ObserverFull(me, mutator, teammates, away);
    }

    private static async Task ObserverFast(Player me, Player mutator, List<(int number, Player player)> teammates, Vector2 away)
    {
        // ---- Strips ----
        Check("a Pile Viewer for every teammate", teammates.All(t => PileViewer.StripCounts(t.player.NetId) != null), PileViewer.DescribeState());
        if (PileViewer.StripAndRow(MutatorId) is { } placed)
        {
            foreach (var (_, other) in teammates)
            {
                if (PileViewer.StripAndRow(other.NetId) is not { } p) continue;
                float expectedX = PileViewer.ExpectedFixedX(other.NetId);
                Check($"P{other.NetId}'s Pile Viewer at its default spot (cap or after the name)", p.Strip.Position.X >= p.Row.End.X
                    && Mathf.Abs(p.Strip.Position.X - expectedX) < 1.5f && Mathf.Abs(p.Strip.GetCenter().Y - p.Row.GetCenter().Y) < 12f
                    && p.Strip.Size.Y <= p.Row.Size.Y,
                    $"Pile Viewer {p.Strip}, name block {p.Row}, cap {PileViewer.FixedOriginCap():0}, expected x {expectedX:0}");
            }
            await Mouse(placed.Strip.GetCenter(), null);
            await Wait(0.1);
            Check("hovering a Pile Viewer lights that teammate's frame", PileViewer.RowHighlighted(MutatorId));
            Check("the name's own hand-count icon is hidden (the Pile Viewer shows it)", !PileViewer.NativeHandCountShown(MutatorId));
            // The mutator's new relics fly in beside its name: above the Pile Viewer, not hidden behind it.
            await WaitUntil(() => PileViewer.RowAnimationZ(MutatorId) != null, 5);
            await Wait(0.1); // raised on the Pile Viewer's next frame
            int? animationZ = PileViewer.RowAnimationZ(MutatorId);
            Check("relics gained beside the name draw above the Pile Viewer", animationZ > 100, $"animation z {animationZ?.ToString() ?? "none seen"}");
            await WaitUntil(() => mutator.Creature.MaxHp >= 110, 10);
            await Wait(0.2);
            if (PileViewer.StripAndRow(MutatorId) is { } grown)
            {
                float bar = PileViewer.HealthBarEnd(MutatorId);
                Check("a longer health bar (more max HP) pushes the Pile Viewer right of the cap", mutator.Creature.MaxHp >= 110
                    && grown.Strip.Position.X > bar && grown.Strip.Position.X - bar < 30f && grown.Strip.Position.X > PileViewer.FixedOriginCap() + 1f,
                    $"max HP {mutator.Creature.MaxHp}, health bar ends {bar:0}, cap {PileViewer.FixedOriginCap():0}, Pile Viewer starts {grown.Strip.Position.X:0}");
            }
            await Mouse(new Vector2(960f, 300f), null);
            await Wait(0.1);
            Check("…and it goes out when the mouse leaves", !PileViewer.RowHighlighted(MutatorId));
        }
        await WaitUntil(() => PileType.Exhaust.GetPile(mutator).Cards.Count > 0, 15);
        await Wait(0.2);
        string expected = string.Join("/", PileViewer.StripSlots.Select(p => p.GetPile(mutator).Cards.Count));
        string shown = string.Join("/", PileViewer.StripCounts(mutator.NetId) ?? Array.Empty<string>());
        Check("strip counts follow the teammate's piles live", shown == expected, $"strip {shown}, piles {expected}");

        PileViewer.SimulatePress(PileViewer.StripIndexOf(mutator.NetId), PileType.Draw);
        await WaitUntil(() => NCapstoneContainer.Instance?.InUse == true, 3);
        Check("clicking a slot opens that pile", PileViewer.DescribeOpenView().Contains("Draw pile of player 1000"), PileViewer.DescribeOpenView());
        PileViewer.SimulatePress(PileViewer.StripIndexOf(mutator.NetId), PileType.Draw);
        await WaitUntil(() => NCapstoneContainer.Instance?.InUse != true, 3);
        Check("clicking it again closes it", NCapstoneContainer.Instance?.InUse != true);
        // Empty piles: the game's bubble ("My Discard pile is empty.") names the teammate instead.
        string owner = mutator.Character.Title.GetFormattedText();
        string[] bubbles = new[] { "OPEN_EMPTY_DRAW", "OPEN_EMPTY_DISCARD" }.Select(k =>
            PileViewer.PersonalizeBubble(new LocString("combat_messages", k).GetFormattedText(), owner)).ToArray();
        Check("empty-pile bubbles name the teammate", bubbles.All(b => b.StartsWith(owner + "'s ") && !b.Contains("My ")),
            string.Join(" | ", bubbles));

        // The game briefly shows a teammate's newly obtained relics beside their name, drawn over the Pile Viewer, and
        // they take the mouse where they're drawn (what you see is what you click): drag once they're gone.
        await WaitUntil(() => !PileViewer.RowAnimating(MutatorId), 10);
        if (PileViewer.SlotRect(MutatorId, PileType.Discard) is Rect2 slot)
        {
            Vector2 start = slot.GetCenter();
            await Mouse(start, null);
            Control? under = ((SceneTree)Engine.GetMainLoop()).Root.GuiGetHoveredControl();
            string hit = $"slot {slot}, under the mouse: {under?.GetPath().ToString() ?? "nothing"}";
            await Mouse(start, true);
            await Mouse(start + new Vector2(30f, -20f), null);
            await Mouse(start + new Vector2(60f, -40f), null);
            await Mouse(start + new Vector2(60f, -40f), false);
            await Wait(0.2);
            Check("dragging a Pile Viewer moves it and opens nothing",
                Settings.GetOffset(MutatorId).Length() > 50f && NCapstoneContainer.Instance?.InUse != true,
                $"offset {Settings.GetOffset(MutatorId)}; {hit}");
        }

        // ---- F1 menu ----
        SettingsMenu.Open();
        var beforeScale = PileViewer.StripAndRow(MutatorId);
        SettingsMenu.ScaleSlider!.Value = 150;
        await Wait(0.1);
        if (beforeScale is { } b0 && PileViewer.StripAndRow(MutatorId) is { } b1)
        {
            Vector2 leftMid0 = new(b0.Strip.Position.X, b0.Strip.GetCenter().Y), leftMid1 = new(b1.Strip.Position.X, b1.Strip.GetCenter().Y);
            Check("resizing keeps the centre of the left edge in place", leftMid0.DistanceTo(leftMid1) < 1.5f && b1.Strip.Size.X > b0.Strip.Size.X * 1.4f,
                $"{leftMid0} → {leftMid1}, width {b0.Strip.Size.X:0} → {b1.Strip.Size.X:0}");
        }
        Check("size slider scales the Pile Viewers", Mathf.IsEqualApprox(PileViewer.StripScaleOf(MutatorId).X, 1.5f * PileViewer.BaseScale), $"{PileViewer.StripScaleOf(MutatorId)}");
        SettingsMenu.ScaleSlider.Value = 100;
        SettingsMenu.ResetPositionButton!.EmitSignal(BaseButton.SignalName.Pressed);
        Check("reset strip positions", Settings.GetOffset(MutatorId) == Vector2.Zero);
        SettingsMenu.AlignmentButtons![1].ButtonPressed = true;
        await Wait(0.1);
        if (PileViewer.StripAndRow(MutatorId) is { } floated && PileViewer.CreatureScreenPos(MutatorId) is Vector2 body)
        {
            Check("Float alignment: centred above the character", Settings.Alignment == Settings.PileViewerAlignment.Float
                && Mathf.Abs(floated.Strip.GetCenter().X - body.X) < 3f && floated.Strip.End.Y < body.Y,
                $"Pile Viewer {floated.Strip}, character at {body}");
        }
        SettingsMenu.AlignmentButtons[0].ButtonPressed = true;
        await Wait(0.1);
        if (PileViewer.StripAndRow(MutatorId) is { } fixedAgain)
        {
            Check("Fixed alignment: back at its default spot", Settings.Alignment == Settings.PileViewerAlignment.Fixed
                && Mathf.Abs(fixedAgain.Strip.Position.X - PileViewer.ExpectedFixedX(MutatorId)) < 1.5f,
                $"Pile Viewer {fixedAgain.Strip}, name block {fixedAgain.Row}");
        }
        // Hand Viewer off: no Hand slot, so the Pile Viewer is back to 3 slots wide.
        float withHand = PileViewer.StripAndRow(MutatorId)?.Strip.Size.X ?? 0f;
        SettingsMenu.HandViewerToggle!.ButtonPressed = false;
        await Wait(0.2);
        float withoutHand = PileViewer.StripAndRow(MutatorId)?.Strip.Size.X ?? 0f;
        Check("Hand Viewer off: the Pile Viewer shrinks to 3 slots", withoutHand > 0f && withoutHand < withHand * 0.85f,
            $"width {withHand:0} → {withoutHand:0}");
        SettingsMenu.HandViewerToggle.ButtonPressed = true;
        SettingsMenu.CardSignalButtons![1].ButtonPressed = true;
        await Wait(0.1);
        Check("card signals: Off / Receive only / Send + Receive; Receive only doesn't send", SettingsMenu.CardSignalButtons.Length == 3
            && Settings.CardSignals == Settings.CardSignalMode.ReceiveOnly && !Settings.SendsCardSignals && Settings.ReceivesCardSignals,
            $"{Settings.CardSignals}");
        SettingsMenu.CardSignalButtons[2].ButtonPressed = true;
        SettingsMenu.MaxHandViewersButtons![1].ButtonPressed = true;
        await Wait(0.05);
        Check("Max Hand Viewers: 1 | 2 | 3 buttons", SettingsMenu.MaxHandViewersButtons.Length == 3 && Settings.MaxHandViewers == 2, $"{Settings.MaxHandViewers}");
        SettingsMenu.MaxHandViewersButtons[0].ButtonPressed = true;
        await Wait(0.2);
        var (nativeCircle, ourCircle) = PileViewer.BadgeCircles(PileType.Draw);
        Check("bigger number badges, at most half the icon wide", ourCircle > nativeCircle * 1.1f && ourCircle <= PileViewer.SlotIconWidth / 2f + 0.01f,
            $"badge circle {nativeCircle:0.#} → {ourCircle:0.#} (icon {PileViewer.SlotIconWidth})");
        // The panel pads the slots by 4 with a 1-unit border: badge art must end inside the border (-3 .. icon + 3).
        var artRows = new[] { PileType.Draw, PileType.Hand, PileType.Discard }.Select(PileViewer.BadgeArtRows).ToArray();
        Check("badges stay inside the Pile Viewer's border", artRows.All(r => r.Top >= -3f && r.Bottom <= PileViewer.SlotIconWidth + 3f),
            string.Join(", ", artRows.Select(r => $"{r.Top:0.#}..{r.Bottom:0.#}")));
        SettingsMenu.EnabledToggle!.ButtonPressed = false;
        await Wait(0.2);
        Check("Pile Viewer off hides them but keeps the hand", PileViewer.StripCounts(MutatorId) == null && HandViewer.ShownCards().Count > 0);
        SettingsMenu.EnabledToggle.ButtonPressed = true;
        await Wait(0.2);

        // ---- Hotkeys ----
        int row = MutatorRow;
        int discardSlot = Array.IndexOf(Hotkeys.Slots, PileType.Discard);
        SelectHotkeyRow(row);
        SettingsMenu.GetHotkeyButton(PileType.Draw)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressRowKey(row, Key.C);
        Check("conflicting binding is not saved", Hotkeys.Active[row, 0]?.ToString() == RowKey(row, "Z") && SettingsMenu.HotkeyStatus.StartsWith("Not saved"), SettingsMenu.HotkeyStatus);
        SettingsMenu.GetHotkeyButton(PileType.Discard)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressRowKey(row, Key.B);
        Check("resolving the conflict saves both", Hotkeys.Active[row, 0]?.ToString() == RowKey(row, "C")
            && Hotkeys.Active[row, discardSlot]?.ToString() == RowKey(row, "B"));
        SettingsMenu.GetHotkeyButton(PileType.Discard)!.EmitSignal(BaseButton.SignalName.Pressed);
        await MouseButtonPress(MouseButton.Xbutton1);
        Check("a mouse button can be bound", Hotkeys.Active[row, discardSlot]?.ToString() == "Mouse 4");
        Check("menu shows the version", SettingsMenu.VersionText.StartsWith("v0."), SettingsMenu.VersionText);
        SettingsMenu.Close();
        await MouseButtonPress(MouseButton.Xbutton1);
        await WaitUntil(() => NCapstoneContainer.Instance?.InUse == true, 2);
        Check("mouse-button hotkey opens the pile", PileViewer.DescribeOpenView().Contains("Discard pile of player 1000"), PileViewer.DescribeOpenView());
        await MouseButtonPress(MouseButton.Xbutton1);
        await PressRowKey(row, Key.C);
        await WaitUntil(() => NCapstoneContainer.Instance?.InUse == true, 2);
        Check("key hotkey opens the pile", PileViewer.DescribeOpenView().Contains("Draw pile of player 1000"), PileViewer.DescribeOpenView());
        await PressRowKey(row, Key.C);
        NDevConsole.Instance.ShowConsole();
        await PressRowKey(row, Key.C);
        Check("hotkeys are ignored while the console is open", NCapstoneContainer.Instance?.InUse != true);
        NDevConsole.Instance.HideConsole();
        Hotkeys.ResetDefaults(hand: false);

        // ---- Hand: default view and rendering ----
        Check("teammate's hand shown by default (lowest player number)", HandViewer.ViewedNetId == teammates[0].player.NetId, $"{HandViewer.ViewedNetId}");
        HandViewer.View(null);
        await Wait(0.2);
        Check("eye of a hand not shown has the red slash", PileViewer.EyeSlashed(MutatorId));
        HandViewer.View(MutatorId);
        await Wait(0.3);
        Check("eye of a shown hand is the plain eye", !PileViewer.EyeSlashed(MutatorId));
        var cards = HandViewer.ShownCards();
        var hand = PileType.Hand.GetPile(mutator).Cards;
        Check("every card of their hand is drawn, in order", cards.Select(c => c.Model).SequenceEqual(hand), $"{cards.Count} of {hand.Count}");
        var wrongCost = cards.Where(c => !c.Model.EnergyCost.CostsX && c.Model.EnergyCost.GetWithModifiers(CostModifiers.All) >= 0
            && c.Cost != c.Model.EnergyCost.GetWithModifiers(CostModifiers.All).ToString()).Select(c => $"{c.Model.Id.Entry}={c.Cost}").ToList();
        Check("costs drawn from their state", wrongCost.Count == 0, string.Join(", ", wrongCost));

        // ---- Hide hand ----
        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(0.1);
        Check("Hide hand hides your hand", HandViewer.HideOwnHand && NPlayerHand.Instance!.CardHolderContainer.Modulate.A < 0.01f);
        HandViewer.HideButton?.EmitSignal(BaseButton.SignalName.Pressed);
        Settings.HideHandButtonEnabled = false;
        await Wait(0.1);
        await PressKey(Key.H);
        bool hidden = HandViewer.HideOwnHand;
        await PressKey(Key.H);
        Check("H works with the button hidden", hidden && !HandViewer.HideOwnHand);
        Settings.HideHandButtonEnabled = true;
        Settings.ShowSelf = true;
        await Wait(0.2);
        PileViewer.SimulatePress(PileViewer.StripIndexOf(me.NetId), PileType.Hand);
        Check("your own strip's eye = Hide hand, teammate view kept", HandViewer.HideOwnHand && HandViewer.ViewedNetId == MutatorId);
        PileViewer.SimulatePress(PileViewer.StripIndexOf(me.NetId), PileType.Hand);
        Settings.ShowSelf = false;
        await Wait(0.2);

        // ---- Hover delay, closeup ----
        Vector2 Peek(int i) => HandViewer.PeekPoint(i)!.Value;
        Settings.HoverDelay = 0.25f;
        await Mouse(away, null);
        await Wait(0.3);
        await Mouse(Peek(1), null);
        await Wait(0.05);
        Check("hover waits for the delay", HandViewer.HoveredId != null && HandViewer.CloseupId == null, HandViewer.DescribeHover());
        await Wait(0.3);
        Check("closeup after the delay", HandViewer.CloseupId != null && HandViewer.CloseupId == HandViewer.HoveredId, HandViewer.DescribeHover());
        Check("hovering a teammate's card lights their frame", PileViewer.RowHighlighted(MutatorId), HandViewer.DescribeHover());
        if (HandViewer.CloseupGeometry() is { } g)
        {
            (float bottom, float ownTop, float modulate) = g;
            float lifted = (1f - 0.6f * Settings.HandShadowWhenHidden) / (1f - 0.6f * Settings.HandShadow);
            // Above your cards under it, or (by design) down to just below the mouse so it never slips out from under it.
            float limit = Mathf.Max(ownTop, HandViewer.LastMouse.Y + 4f) + 2f;
            Check("closeup sits above your hand", bottom <= limit, $"bottom {bottom:0}, your cards {ownTop:0}, mouse {HandViewer.LastMouse.Y:0}");
            Check("closeup uses the 'when hand hidden' shadow", Mathf.Abs(modulate - lifted) < 0.02f, $"{modulate:0.00} vs {lifted:0.00}");
        }
        await Mouse(Peek(2), null);
        await Wait(0.1);
        Check("moving sideways is immediate", HandViewer.CloseupId != null && HandViewer.CloseupId == HandViewer.HoveredId, HandViewer.DescribeHover());
        await Mouse(away, null);
        await Wait(0.3);
        await Mouse(Peek(3), null);
        await Mouse(Peek(3), true);
        await Mouse(Peek(3), false);
        Check("click skips the delay", HandViewer.CloseupId != null, HandViewer.DescribeHover());
        await Mouse(away, null);
        Settings.HoverDelay = 0.1f;
        await Wait(0.3);

        // ---- A closeup's tooltips sit beside the card, not behind it (hand shown, then hidden) ----
        foreach (bool handHidden in new[] { false, true })
        {
            if (HandViewer.HideOwnHand != handHidden) HandViewer.ToggleHideOwnHand();
            int tipped = HandViewer.MiddleIndexOf(c => c.HoverTips.Any());
            if (tipped < 0) break;
            await Mouse(away, null);
            await Wait(0.3);
            await Mouse(Peek(tipped), null);
            await Wait(0.4);
            var tips = HandViewer.CloseupTips();
            bool beside = tips is { } t && (t.Tips.Position.X >= t.Card.End.X - 1f || t.Tips.End.X <= t.Card.Position.X + 1f);
            Check($"a closeup's tooltips sit beside the card{(handHidden ? " (hand hidden)" : "")}", beside,
                tips is { } u ? $"card {u.Card}, tips {u.Tips}" : $"no tooltips ({HandViewer.DescribeHover()})");
        }
        if (HandViewer.HideOwnHand) HandViewer.ToggleHideOwnHand();
        await Mouse(away, null);
        await Wait(0.3);

        // ---- Hand hidden: nothing covers the front hand, so its closeup isn't moved up ----
        HandViewer.ToggleHideOwnHand();
        await Mouse(Peek(2), null);
        await Wait(0.3);
        var frontLift = HandViewer.CloseupLift();
        Check("hand hidden: the front hand's closeup stays (only kept on screen)", StaysOnScreen(frontLift), $"{frontLift}");
        await Mouse(away, null);
        HandViewer.ToggleHideOwnHand();
        await Wait(0.3);

        // ---- Your own card play wins ----
        if (NPlayerHand.Instance!.ActiveHolders.FirstOrDefault() is NHandCardHolder own)
        {
            Vector2 start = own.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(start, null);
            await Mouse(start, true);
            for (int i = 1; i <= 5; i++) await Mouse(start + new Vector2(0f, -35f * i), null);
            await Mouse(Peek(1), null);
            await Wait(0.2);
            Check("teammate cards ignore the mouse while you play a card", HandViewer.InputBlocked && HandViewer.HoveredId == null, HandViewer.DescribeHover());
            await RightClick(Peek(1));
            await Mouse(Peek(1), false);
            await Mouse(away, null);
            await WaitUntil(() => !HandViewer.InputBlocked, 3);
            Check("and respond again afterwards", !HandViewer.InputBlocked);
        }

        // ---- Aim preview (Vulnerable on the enemy changes the number) ----
        Send(me, $"power {Id<VulnerablePower>(ModelDb.AllPowers)} 2 {CreatureIndex(FirstEnemy)}");
        await WaitUntil(() => FirstEnemy.Powers.Any(p => p is VulnerablePower), 5);
        int attack = HandViewer.MiddleIndexOf(c => c.TargetType == TargetType.AnyEnemy && c.DynamicVars.ContainsKey("Damage"));
        if (Check("teammate has a single-target attack", attack >= 0))
        {
            int energy = mutator.PlayerCombatState!.Energy, count = hand.Count, said = PileViewer.SaidCount;
            Vector2 from = Peek(attack);
            Vector2 to = NCombatRoom.Instance!.GetCreatureNode(FirstEnemy)!.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(from, null);
            await Mouse(from, true);
            for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
            await Wait(0.3);
            string aimed = HandViewer.DescribeDragCard();
            Check("dragging a teammate card aims with the game's arrow", HandViewer.IsAiming, aimed);
            Check("aimed numbers include the target's Vulnerable", aimed.Contains("[green]"), aimed);
            await Mouse(to, false);
            await Wait(0.3);
            Check("letting go plays nothing", !HandViewer.IsDragging && hand.Count == count && mutator.PlayerCombatState.Energy == energy);
            Check("…and says it's their card", PileViewer.SaidCount > said);
            await Mouse(away, null);
        }

    }

    private static async Task ObserverFull(Player me, Player mutator, List<(int number, Player player)> teammates, Vector2 away)
    {
        Vector2 Peek(int i) => HandViewer.PeekPoint(i)!.Value;
        HandViewer.View(MutatorId);

        // ---- Accuracy vs the teammate's own screen (REF1/VIEW1 compared by the runner) ----
        int mi = CreatureIndex(mutator.Creature);
        Send(me, $"power {Id<StrengthPower>(ModelDb.AllPowers)} 3 {mi}");
        Send(me, $"power {Id<WeakPower>(ModelDb.AllPowers)} 2 {mi}");
        await WaitUntil(() => mutator.Creature.Powers.Any(p => p is WeakPower), 5);
        if (await Ask(1))
        {
            await Wait(0.8);
            foreach (string line in HandViewer.DumpTeammateCards()) Log("VIEW1 " + line);

            // Beat Into Shape: Forge counts the *teammate's* hits on that enemy this turn.
            int bis = HandViewer.IndexOf(c => c is BeatIntoShape);
            if (Check("Beat Into Shape in their hand", bis >= 0))
            {
                CardModel model = PileType.Hand.GetPile(mutator).Cards.First(c => c is BeatIntoShape);
                decimal forge = ((CalculatedVar)model.DynamicVars["CalculatedForge"]).Calculate(FirstEnemy);
                Vector2 from = Peek(bis), to = NCombatRoom.Instance!.GetCreatureNode(FirstEnemy)!.Hitbox.GetGlobalRect().GetCenter();
                await Mouse(from, null);
                await Wait(0.2);
                string hover = $"from {from} {HandViewer.DescribeHover()}";
                await Mouse(from, true);
                for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
                await Wait(0.3);
                string text = HandViewer.DescribeDragCard();
                var m = System.Text.RegularExpressions.Regex.Match(text, @"Forge\[/gold\] (?:\[\w+\])?(\d+)");
                Check("Beat Into Shape forges for the teammate's hits", forge > 5 && m.Success && m.Groups[1].Value == forge.ToString("0"), $"expected {forge}, card: {text} ({hover})");
                await RightClick(to);
                await Mouse(to, false);
                await Mouse(away, null);
                await Wait(0.3);
            }
            int slash = HandViewer.IndexOf(c => c is EchoingSlash);
            if (Check("Echoing Slash in their hand", slash >= 0))
            {
                string before = HandViewer.DumpTeammateCards().First(l => l.StartsWith("ECHOING_SLASH"));
                Vector2 from = Peek(slash);
                await Mouse(from, null);
                await Wait(0.2);
                string hover = $"from {from} {HandViewer.DescribeHover()}";
                await Mouse(from, true);
                for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(new Vector2(960f, 350f), i / 6f), null);
                await Wait(0.3);
                string aimed = HandViewer.DescribeDragCard();
                Check("all-enemies preview shows the Vulnerable number", aimed != before && aimed.Contains("[green]"), $"{before} → {aimed} ({hover})");
                await RightClick(new Vector2(960f, 350f));
                await Mouse(new Vector2(960f, 350f), false);
                await Mouse(away, null);
                await Wait(0.3);
            }
            // Lift (any ally): aiming it at you lights both frames, the card's owner and the target.
            int lift = HandViewer.IndexOf(c => c is Lift);
            if (Check("Lift in their hand", lift >= 0))
            {
                Vector2 from = Peek(lift), to = NCombatRoom.Instance!.GetCreatureNode(me.Creature)!.Hitbox.GetGlobalRect().GetCenter();
                // Lift is the last card of a full hand: its corner is under the End Turn button (drawn on top), so
                // press the part of it that shows.
                Rect2 endTurnRect = NCombatRoom.Instance!.Ui.EndTurnButton.GetGlobalRect();
                if (endTurnRect.HasPoint(from)) from.X = endTurnRect.Position.X - 30f;
                await Mouse(from, null);
                string liftUnder = ((SceneTree)Engine.GetMainLoop()).Root.GuiGetHoveredControl()?.GetPath().ToString() ?? "nothing";
                await Mouse(from, true);
                for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
                await Wait(0.3);
                string aimed = HandViewer.DescribeDragCard();
                Check("Lift aims at an ally with the game's arrow", HandViewer.IsAiming, $"{aimed}; pressed at {from} on {liftUnder}; End Turn {NCombatRoom.Instance!.Ui.EndTurnButton.GetGlobalRect()}");
                Check("...lighting the owner's frame and the target's", PileViewer.RowHighlighted(MutatorId) && PileViewer.RowHighlighted(me),
                    $"owner {PileViewer.RowHighlighted(MutatorId)}, target {PileViewer.RowHighlighted(me)}");
                await RightClick(to);
                await Mouse(to, false);
                await Mouse(away, null);
                await Wait(0.3);
            }
        }

        // ---- Glow follows their turn and energy ----
        if (await Ask(2))
        {
            // Their end of turn reaches us shortly after their reply; the glow refreshes 4x a second.
            await WaitUntil(() => HandViewer.ShownCards().All(c => c.Glow == "-"), 2);
            Check("no glow once they've ended their turn", HandViewer.ShownCards().All(c => c.Glow == "-"), HandViewer.DescribeTeammateGlow());
        }
        if (await Ask(3))
        {
            await WaitUntil(() => HandViewer.ShownCards().Any(c => c.Glow == "playable"), 2);
            Check("glow back after they undo it", HandViewer.ShownCards().Any(c => c.Glow == "playable"), HandViewer.DescribeTeammateGlow());
        }
        if (await Ask(4))
        {
            await Wait(1.0);
            Log($"VIEW2 energy={mutator.PlayerCombatState!.Energy} {HandViewer.DescribeTeammateGlow()}");
        }

        // ---- Trying out one of their cards: they see it over your head; they play it while you aim ----
        int trying = HandViewer.IndexOf(c => c is BeatIntoShape);
        if (Check("Beat Into Shape still in their hand", trying >= 0))
        {
            Vector2 from = Peek(trying), to = NCombatRoom.Instance!.GetCreatureNode(FirstEnemy)!.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(from, null);
            await Mouse(from, true);
            for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
            await Wait(0.3);
            Check("aiming one of their cards", HandViewer.IsAiming, HandViewer.DescribeDragCard());
            await Ask(5);
            await Wait(0.5);
            Check("they play the card you're aiming: your aim ends cleanly", !HandViewer.IsDragging && !HandViewer.IsAiming
                && !NTargetManager.Instance.IsInSelection && !PileType.Hand.GetPile(mutator).Cards.Any(c => c is BeatIntoShape),
                $"dragging={HandViewer.IsDragging} aiming={HandViewer.IsAiming} selection={NTargetManager.Instance.IsInSelection}");
            await Mouse(to, false);
            await Mouse(away, null);
            await Wait(0.3);
        }

        // ---- Hover delay: nothing goes over your head until the closeup shows (they watch the whole time) ----
        if (await Ask(6))
        {
            Settings.HoverDelay = 0.9f;
            int middle = HandViewer.MiddleIndexOf(_ => true);
            await Mouse(Peek(middle), null);
            await Wait(0.5); // well inside the 0.9 s delay
            bool waiting = HandViewer.HoveredId != null && HandViewer.CloseupId == null;
            string hover = HandViewer.DescribeHover();
            await Mouse(away, null);
            await Wait(0.3);
            Check("hovered within the delay: no closeup yet", waiting, hover);
            await Ask(7);
            Settings.HoverDelay = 0f;
            await Mouse(Peek(middle), null);
            await Wait(0.2);
            Check("instant hover: closeup shows", HandViewer.CloseupId != null, HandViewer.DescribeHover());
            var tracker = RunManager.Instance.HoveredModelTracker;
            Check("...and you send it (Send + Receive)", tracker.GetHoveredModel(me.NetId)?.Id.Entry == HandViewer.CloseupId, $"{tracker.GetHoveredModel(me.NetId)?.Id.Entry ?? "nothing"}");
            await Ask(8);
            Settings.CardSignals = Settings.CardSignalMode.ReceiveOnly;
            await Wait(0.2);
            Check("card signals Receive only: nothing sent", HandViewer.CloseupId != null && tracker.GetHoveredModel(me.NetId) == null,
                $"closeup {HandViewer.CloseupId}, sent {tracker.GetHoveredModel(me.NetId)?.Id.Entry ?? "nothing"}");
            Settings.CardSignals = Settings.CardSignalMode.SendAndReceive;
            await Mouse(away, null);
            Settings.HoverDelay = 0.1f;
            await Wait(0.3);
        }

        // ---- Holding one of their cards that doesn't aim (no drag): it blinks gold on their screen ----
        int still = HandViewer.MiddleIndexOf(c => c.TargetType is not (TargetType.AnyEnemy or TargetType.AnyAlly));
        if (Check("they have a card that doesn't aim", still >= 0))
        {
            Vector2 at = Peek(still);
            await Mouse(at, null);
            await Mouse(at, true);
            await Ask(9);
            await Mouse(at, false);
            await Mouse(away, null);
            await Wait(0.3);
        }

        // ---- Network: card signals ride on the game's own cursor messages (at most one per 50 ms) ----
        async Task<int> Sweep(Settings.CardSignalMode mode)
        {
            Settings.CardSignals = mode;
            Settings.HoverDelay = 0f;
            await Mouse(away, null);
            await Wait(0.3);
            int before = PeerInputSends;
            Vector2 left = HandViewer.PeekPointIn(MutatorId, rightmost: false)!.Value, right = HandViewer.PeekPointIn(MutatorId, rightmost: true)!.Value;
            for (int i = 0; i <= 20; i++) await Mouse(left.Lerp(right, i / 20f), null);
            for (int i = 20; i >= 0; i--) await Mouse(left.Lerp(right, i / 20f), null);
            await Mouse(away, null);
            await Wait(0.3);
            return PeerInputSends - before;
        }
        int quiet = await Sweep(Settings.CardSignalMode.Off);
        int signalling = await Sweep(Settings.CardSignalMode.SendAndReceive);
        Settings.HoverDelay = 0.1f;
        Log($"network: {quiet} peer-input messages with card signals off, {signalling} with Send + Receive (same 42 mouse moves across their cards)");
        Check("card signals add no network messages (they ride on the game's cursor updates)", quiet > 0 && signalling <= quiet + 3,
            $"{quiet} messages with signals off, {signalling} with Send + Receive, over 42 mouse moves across their cards");

        // ---- Hotkey regressions ----
        int row = MutatorRow;
        SettingsMenu.Open();
        SelectHotkeyRow(row);
        SettingsMenu.GetHotkeyButton(PileType.Draw)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressRowKey(row, Key.S);
        Check("a game key saves with a warning", Hotkeys.Active[row, 0]?.ToString() == RowKey(row, "S") && SettingsMenu.HotkeyStatus.Contains("used by the game"), SettingsMenu.HotkeyStatus);
        SettingsMenu.Close();
        await PressRowKey(row, Key.S);
        Check("…and the game keeps the key", !PileViewer.DescribeOpenView().Contains("player 1000"), PileViewer.DescribeOpenView());
        if (NCapstoneContainer.Instance?.InUse == true) await PressKey(Key.Escape);
        var mkb = (Dictionary<StringName, Key>)Traverse.Create(NInputManager.Instance!).Field("_mKbInputMap").GetValue();
        var movedOffD = mkb.Where(kv => kv.Value == Key.D).Select(kv => kv.Key).ToList();
        foreach (StringName action in movedOffD) mkb[action] = Key.P;
        SettingsMenu.Open();
        SelectHotkeyRow(row);
        SettingsMenu.GetHotkeyButton(PileType.Draw)!.EmitSignal(BaseButton.SignalName.Pressed);
        await PressRowKey(row, Key.D);
        Check("a key the game no longer uses is free", Hotkeys.Active[row, 0]?.ToString() == RowKey(row, "D") && !SettingsMenu.HotkeyStatus.Contains("used by the game"), SettingsMenu.HotkeyStatus);
        SettingsMenu.Close();
        await PressRowKey(row, Key.D);
        await WaitUntil(() => NCapstoneContainer.Instance?.InUse == true, 2);
        Check("…and fires", PileViewer.DescribeOpenView().Contains("Draw pile of player 1000"), PileViewer.DescribeOpenView());
        await PressRowKey(row, Key.D);
        foreach (StringName action in movedOffD) mkb[action] = Key.D;
        if (teammates.Count < Hotkeys.MaxTeammates)
        {
            int absent = Hotkeys.MaxTeammates - 1; // last row: no such teammate in a 2-3 player game
            SettingsMenu.Open();
            SelectHotkeyRow(row);
            SettingsMenu.GetHotkeyButton(PileType.Draw)!.EmitSignal(BaseButton.SignalName.Pressed);
            await PressRowKey(absent, Key.Z);
            Check("duplicate with an absent teammate saves with a warning", Hotkeys.Active[row, 0]?.ToString() == RowKey(absent, "Z") && SettingsMenu.HotkeyStatus.Contains("gets it"), SettingsMenu.HotkeyStatus);
            SettingsMenu.Close();
        }
        Hotkeys.ResetDefaults(hand: false);
        HandViewer.View(null);
        await PressRowKey(row, Key.H);
        Check("hand hotkey shows that teammate's hand", HandViewer.ViewedNetId == MutatorId);
        await PressRowKey(row, Key.H);
        Check("…and hides it again", HandViewer.ViewedNetId == null);

        // ---- Hover delay ends ----
        HandViewer.View(MutatorId);
        await Wait(0.3);
        Settings.HoverDelay = Settings.MaxHoverDelay;
        await Mouse(away, null);
        await Wait(0.4);
        await Mouse(Peek(0), null);
        await Wait(0.6);
        Check("hover delay at max = disabled", HandViewer.HoveredId != null && HandViewer.CloseupId == null, HandViewer.DescribeHover());
        await Mouse(away, null);
        await Wait(0.4);
        Settings.HoverDelay = 0f;
        await Mouse(Peek(0), null);
        await Wait(0.05);
        Check("hover delay at 0 = instant", HandViewer.CloseupId != null, HandViewer.DescribeHover());
        await Mouse(away, null);
        Settings.HoverDelay = 0.1f;

        // ---- 3rd player (REF3/VIEW3 compared by the runner) ----
        if (teammates.FirstOrDefault(t => t.player.NetId == 1001).player is Player third)
        {
            PileViewer.SimulatePress(PileViewer.StripIndexOf(1001), PileType.Hand);
            await Wait(0.5);
            Check("eye switches to another teammate", HandViewer.ViewedNetId == 1001);
            foreach (string line in HandViewer.DumpTeammateCards()) Log("VIEW3 " + line);

            // ---- Max Hand Viewers: stacked hands, the latest click on top ----
            Settings.MaxHandViewers = 2;
            HandViewer.View(MutatorId);
            PileViewer.SimulatePress(PileViewer.StripIndexOf(1001), PileType.Hand);
            await Mouse(away, null);
            await Wait(0.6);
            var stack = HandViewer.StackedHands();
            string desc = string.Join("; ", stack.Select(s => $"{s.NetId}: layer {s.LayerIndex}, raised {s.Raise:0} (expected {s.Expected:0})"));
            bool twoInOrder = stack.Select(s => s.NetId).SequenceEqual(new ulong[] { MutatorId, 1001 });
            Check("two hands shown, the latest click on top", twoInOrder, desc);
            Check("each hand one '% of card visible' step above the one below (20%, 40%)",
                stack.Count == 2 && stack.All(s => Mathf.Abs(s.Raise - s.Expected) < 2f), desc);
            Check("the lower hand is drawn in front", twoInOrder && stack[0].LayerIndex > stack[1].LayerIndex, desc);
            Check("both shown hands' eyes are plain", !PileViewer.EyeSlashed(MutatorId) && !PileViewer.EyeSlashed(1001));
            await Mouse(HandViewer.PeekPoint(HandViewer.MiddleIndexOf(_ => true))!.Value, null);
            await Wait(0.3);
            Check("hovering the top hand lights its owner's frame", HandViewer.HoveredOwnerNetId == 1001 && PileViewer.RowHighlighted(1001UL)
                && !PileViewer.RowHighlighted(MutatorId), HandViewer.DescribeHover());
            await Mouse(away, null);
            await Wait(0.3);
            // Stacked hands reach the End Turn button: it stays on top and takes the mouse across its width.
            Rect2 endRect = NCombatRoom.Instance!.Ui.EndTurnButton.GetGlobalRect();
            var hits = new List<string>();
            foreach (float fx in new[] { 0.1f, 0.5f, 0.9f })
            {
                await Mouse(new Vector2(endRect.Position.X + endRect.Size.X * fx, endRect.GetCenter().Y), null);
                await Wait(0.1);
                hits.Add(((SceneTree)Engine.GetMainLoop()).Root.GuiGetHoveredControl()?.Name ?? "nothing");
            }
            await Mouse(away, null);
            Check("with hands stacked, the End Turn button takes the mouse across its width", hits.All(h => h == "EndTurnButton"), string.Join(", ", hits));
            await Wait(0.2);
            // Sliding from one hand's card onto another hand's card starts the hover delay again.
            Settings.HoverDelay = 0.4f;
            await Mouse(HandViewer.PeekPointIn(MutatorId, rightmost: false)!.Value, null);
            await Wait(0.6);
            bool lowerShown = HandViewer.CloseupId != null && HandViewer.HoveredOwnerNetId == MutatorId;
            await Mouse(HandViewer.PeekPointIn(1001, rightmost: true)!.Value, null);
            await Wait(0.1);
            string moved = HandViewer.DescribeHover();
            bool waits = HandViewer.HoveredOwnerNetId == 1001 && HandViewer.CloseupId == null;
            await Wait(0.5);
            Check("moving onto another hand's card restarts the hover delay", lowerShown && waits && HandViewer.CloseupId != null,
                $"lower hand closeup {lowerShown}; right after the move: {moved}; then: {HandViewer.DescribeHover()}");
            await Mouse(away, null);
            Settings.HoverDelay = 0.1f;
            await Wait(0.3);
            // Hand hidden: a hand further back is partly covered by the front one, so its closeup still rises.
            HandViewer.ToggleHideOwnHand();
            await Mouse(HandViewer.PeekPointIn(1001, rightmost: true)!.Value, null);
            await Wait(0.4);
            var backLift = HandViewer.CloseupLift();
            await Mouse(away, null);
            await Wait(0.3);
            await Mouse(HandViewer.PeekPointIn(MutatorId, rightmost: false)!.Value, null);
            await Wait(0.4);
            var frontLift = HandViewer.CloseupLift();
            Check("hand hidden: a card further back rises, the front hand's stays (only kept on screen)", backLift is { Level: 2 } bl
                && bl.Lift > 20f && bl.AboveScreenBottom >= -0.5f && StaysOnScreen(frontLift), $"back {backLift}, front {frontLift}");
            await Mouse(away, null);
            HandViewer.ToggleHideOwnHand();
            await Wait(0.3);
            PileViewer.SimulatePress(PileViewer.StripIndexOf(MutatorId), PileType.Hand);
            await Wait(0.2);
            Check("its eye closes just that hand", HandViewer.ViewedNetIds.SequenceEqual(new ulong[] { 1001 }), HandViewer.DescribeViewed());
            PileViewer.SimulatePress(PileViewer.StripIndexOf(MutatorId), PileType.Hand);
            await Wait(0.2);
            Check("reopened, it goes on top", HandViewer.ViewedNetIds.SequenceEqual(new ulong[] { 1001, MutatorId }), HandViewer.DescribeViewed());
            Settings.MaxHandViewers = 1;
            await Wait(0.2);
            Check("lowering Max Hand Viewers closes the least recently clicked", HandViewer.ViewedNetIds.SequenceEqual(new ulong[] { MutatorId })
                && PileViewer.EyeSlashed(1001), HandViewer.DescribeViewed());
            HandViewer.View(MutatorId);
        }

        // ---- Next fight: whose hand is shown carries over, Hide hand resets ----
        HandViewer.View(null);
        if (!HandViewer.HideOwnHand) HandViewer.ToggleHideOwnHand();
        Send(me, "win");
        await WaitUntil(() => !CombatManager.Instance.IsInProgress, 20);
        await Wait(1.5);
        Send(me, "fight " + PickEncounter().Id.Entry, inCombat: false);
        await WaitUntil(() => CombatManager.Instance.IsInProgress, 30);
        await Wait(1.0);
        Check("next fight keeps the choice (nobody) and shows your hand",
            CombatManager.Instance.IsInProgress && HandViewer.ViewedNetId == null && !HandViewer.HideOwnHand,
            $"viewing={HandViewer.ViewedNetId?.ToString() ?? "nobody"} hideOwnHand={HandViewer.HideOwnHand}");

        // ---- Dead and spectating (last: you stay dead): nothing of yours to avoid ----
        if (teammates.Any(t => t.player.NetId == 1001))
        {
            Settings.MaxHandViewers = 2;
            Settings.HoverDelay = 0.1f;
            HandViewer.View(MutatorId);
            PileViewer.SimulatePress(PileViewer.StripIndexOf(1001), PileType.Hand);
            Send(me, "die");
            await WaitUntil(() => me.Creature.IsDead, 5);
            await Wait(0.8);
            Check("you're dead and still see two teammates' hands", me.Creature.IsDead && HandViewer.StackedHands().Count == 2,
                $"dead {me.Creature.IsDead}, hands {HandViewer.StackedHands().Count}");
            foreach (bool hidden in new[] { false, true })
            {
                if (HandViewer.HideOwnHand != hidden) HandViewer.ToggleHideOwnHand();
                await Mouse(away, null);
                await Wait(0.3);
                await Mouse(HandViewer.PeekPointIn(1001, rightmost: true)!.Value, null);
                await Wait(0.3);
                var back = HandViewer.CloseupLift();
                await Mouse(away, null);
                await Wait(0.3);
                await Mouse(HandViewer.PeekPointIn(MutatorId, rightmost: false)!.Value, null);
                await Wait(0.3);
                var front = HandViewer.CloseupLift();
                bool frontStays = StaysOnScreen(front);
                bool backRises = back is { Level: 2 } r && r.Lift > 20f && r.AboveScreenBottom >= -0.5f;
                Check($"dead{(hidden ? ", hand hidden" : "")}: the front hand's closeup stays (on screen), a card further back rises",
                    frontStays && backRises, $"front {front}, back {back}");
            }
            await Mouse(away, null);
        }
    }

    /// <summary>A front-hand closeup that stays put, or is only nudged up as far as keeping its bottom on screen.</summary>
    private static bool StaysOnScreen((float Lift, int Level, float AboveScreenBottom)? closeup) =>
        closeup is { Level: 1 } c && c.AboveScreenBottom >= -0.5f && (Mathf.Abs(c.Lift) < 1f || c.AboveScreenBottom < 6f);

    // ---------------- Explore: odd hands and situations, for looking at (screenshots + notes) ----------------

    private const int ExploreSteps = 3;

    private static void Note(string text) => Log("EXPLORE " + text);

    private static int _shot = 100;

    private static async Task Snap(string caption)
    {
        await Wait(0.35);
        Shot(++_shot);
        Log($"SHOTNOTE {_shot} {caption}");
    }

    /// <summary>Teammates: 1000 statuses/curses/X cost (then lock-step steps), 1001 full upgraded hand, 1002 afflictions.</summary>
    private static async Task ExploreTeammate()
    {
        Player me = Me;
        _hostGold0 = Host.Gold;
        var hand = PileType.Hand.GetPile(me);
        string Card<T>() where T : CardModel => Id<T>(ModelDb.AllCards);
        switch (me.NetId)
        {
            case 1000:
                foreach (CardModel c in hand.Cards.ToList()) Send(me, $"remove_card {c.Id.Entry} Hand");
                await WaitUntil(() => hand.Cards.Count == 0, 5);
                foreach (string id in new[] { Card<BladeDance>(), Card<Wound>(), Card<Dazed>(), Card<Regret>(), Card<Doubt>(), Card<Skewer>() })
                    Send(me, "card " + id);
                foreach (string id in new[] { Card<Regret>(), Card<Wound>(), Card<Shiv>() }) Send(me, $"card {id} Discard");
                foreach (string id in new[] { Card<Burn>(), Card<Slimed>() }) Send(me, $"card {id} Exhaust");
                Send(me, "energy 6");
                break;
            case 1001:
                Send(me, "draw 5");
                await WaitUntil(() => hand.Cards.Count >= 10, 5);
                for (int i = 0; i < 3; i++) Send(me, $"upgrade {i}");
                Send(me, "card " + Card<Whirlwind>()); // hand full: where does it go?
                for (int i = 0; i < 12; i++) Send(me, $"card {Card<StrikeRegent>()} Draw"); // a long draw pile screen
                break;
            default:
                string[] afflictions =
                {
                    ModelDb.Affliction<Bound>().Id.Entry, ModelDb.Affliction<Entangled>().Id.Entry, ModelDb.Affliction<Galvanized>().Id.Entry,
                    ModelDb.Affliction<Hexed>().Id.Entry, ModelDb.Affliction<Ringing>().Id.Entry, ModelDb.Affliction<Smog>().Id.Entry,
                    ModelDb.Affliction<Tainted>().Id.Entry,
                };
                Send(me, "draw 2");
                await WaitUntil(() => hand.Cards.Count >= 7, 5);
                for (int i = 0; i < afflictions.Length && i < hand.Cards.Count; i++) Send(me, $"afflict {afflictions[i]} 1 {i}");
                break;
        }
        await Wait(2.0);
        await WaitUntil(() => NPlayerHand.Instance!.ActiveHolders.Count == hand.Cards.Count, 5);
        await Wait(0.5);
        string tag = me.NetId switch { 1000 => "REF4", 1001 => "REF5", _ => "REF6" };
        foreach (string line in HandViewer.DumpOwnHand()) Log($"{tag} {line}");
        Log($"EXPLORE P{me.NetId} own hand {hand.Cards.Count}: {string.Join(", ", hand.Cards.Select(c => c.Id.Entry))}; discard {PileType.Discard.GetPile(me).Cards.Count}, exhaust {PileType.Exhaust.GetPile(me).Cards.Count}");

        if (me.NetId != MutatorId)
        {
            // Wait for the host to finish its steps with 1000, then idle through the second fight.
            await WaitUntil(() => Host.Gold >= _hostGold0 + ExploreSteps + 1, 200);
            return;
        }
        for (int k = 1; k <= ExploreSteps + 1; k++)
        {
            int asked = k == 4 ? k + 1 : k; // the host's 4th gold releases 1001/1002; its 5th asks for the choice
            Player RunHost() => RunManager.Instance.DebugOnlyGetState()!.Players.First(p => p.NetId == 1); // also between fights
            await WaitUntil(() => RunHost().Gold >= _hostGold0 + asked, 120);
            if (RunHost().Gold < _hostGold0 + asked) return;
            if (k == 4)
            {
                // Fight 2: answer first — once the card choice is open, console actions wait behind it.
                int g = me.Gold;
                Send(me, "gold 1");
                await WaitUntil(() => me.Gold > g, 10);
                Send(me, "energy 3");
                Send(me, "card " + Card<Survivor>());
                await WaitUntil(() => hand.Cards.Any(c => c is Survivor), 5);
                if (hand.Cards.FirstOrDefault(c => c is Survivor) is CardModel sv)
                {
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(sv, null));
                }
                await Wait(1.0);
                Log($"EXPLORE P1000 choosing a card to discard (fight 2); hand: {string.Join(", ", hand.Cards.Select(c => c.Id.Entry))}");
                await Wait(20.0); // stay in the choice while the host looks
                return;
            }
            switch (k)
            {
                case 1: // generated cards mid-turn
                    if (hand.Cards.FirstOrDefault(c => c is BladeDance) is CardModel bd)
                    {
                        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(bd, null));
                        await WaitUntil(() => hand.Cards.Any(c => c is Shiv), 5);
                    }
                    Log($"EXPLORE P1000 after Blade Dance: {string.Join(", ", hand.Cards.Select(c => c.Id.Entry))}");
                    break;
                case 2: // empty hand
                    foreach (CardModel c in hand.Cards.ToList()) Send(me, $"remove_card {c.Id.Entry} Hand");
                    await WaitUntil(() => hand.Cards.Count == 0, 5);
                    Log($"EXPLORE P1000 hand emptied: {hand.Cards.Count} cards");
                    break;
                case 3: // dead teammate
                    Send(me, "die");
                    await WaitUntil(() => me.Creature.IsDead, 5);
                    Log($"EXPLORE P1000 dead: {me.Creature.IsDead}; hand {hand.Cards.Count}");
                    break;
            }
            int gold = me.Gold;
            Send(me, "gold 1");
            await WaitUntil(() => me.Gold > gold, 10);
        }
    }

    /// <summary>Host: looks at the odd hands through the Pile Viewer and Hand Viewer; screenshots + notes.</summary>
    private static async Task ExploreHost()
    {
        Player me = Me;
        var teammates = PileViewer.Teammates();
        Player t0 = teammates.First(t => t.player.NetId == MutatorId).player;
        _hostGold0 = me.Gold;
        _mutatorGold0 = t0.Gold;
        Vector2 away = new(960f, 300f);
        Settings.HoverDelay = 0.1f;
        // The teammates' setups land (1000 logs its own hand once its odd cards are in).
        await WaitUntil(() => PileType.Hand.GetPile(t0).Cards.Any(c => c is Skewer), 15);
        await Wait(2.5);
        ulong[] ids = teammates.Select(t => t.player.NetId).ToArray();
        Vector2 enemy = NCombatRoom.Instance!.GetCreatureNode(FirstEnemy)!.Hitbox.GetGlobalRect().GetCenter();

        async Task Hover(ulong id, Func<CardModel, bool> match, string caption)
        {
            if (HandViewer.PeekPointOf(id, match) is not Vector2 at)
            {
                Note($"P{id}: no card for '{caption}'");
                return;
            }
            await Mouse(away, null);
            await Wait(0.2);
            await Mouse(at, null);
            await Wait(0.35);
            Note($"P{id} hover {caption}: {HandViewer.DescribeHover()} | {HandViewer.DescribeFocus()}");
            await Snap($"P{id} hover: {caption}");
            await Mouse(away, null);
            await Wait(0.25);
        }

        async Task Drag(ulong id, Func<CardModel, bool> match, Vector2 to, string caption)
        {
            if (HandViewer.PeekPointOf(id, match) is not Vector2 from)
            {
                Note($"P{id}: no card for '{caption}'");
                return;
            }
            int said = PileViewer.SaidCount;
            await Mouse(away, null);
            await Mouse(from, null);
            await Mouse(from, true);
            for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
            await Wait(0.3);
            Note($"P{id} drag {caption}: aiming={HandViewer.IsAiming} dragging={HandViewer.IsDragging} card={HandViewer.DescribeDragCard()}");
            await Snap($"P{id} drag: {caption}");
            await Mouse(to, false);
            await Wait(0.4);
            Note($"P{id} released {caption}: dragging={HandViewer.IsDragging} bubble said={PileViewer.SaidCount > said}");
            await Mouse(away, null);
            await Wait(0.3);
        }

        // ---- All three hands stacked ----
        Settings.MaxHandViewers = 3;
        HandViewer.ViewMany(ids);
        await Wait(0.6);
        foreach (ulong id in ids)
        {
            foreach (string line in HandViewer.DumpHandOf(id, withGlow: true)) Note($"P{id} view: {line}");
        }
        await Snap("three stacked hands: 1000 statuses/curses/X, 1001 full + upgraded, 1002 afflictions");
        foreach (var (tag, id) in new[] { ("VIEW4", 1000UL), ("VIEW5", 1001UL), ("VIEW6", 1002UL) })
        {
            foreach (string line in HandViewer.DumpHandOf(id, withGlow: false)) Log($"{tag} {line}");
        }
        Note("Pile Viewers: " + PileViewer.DescribeState());
        // Does a full, raised teammate hand cover the End Turn button (and take its clicks)?
        Control endTurn = NCombatRoom.Instance!.Ui.EndTurnButton;
        Note("CombatUi children (index: name z): " + string.Join(", ", endTurn.GetParent().GetChildren().OfType<CanvasItem>()
            .Select(c => $"{c.GetIndex()}: {c.Name} z{c.ZIndex}")));
        Rect2 endRect = endTurn.GetGlobalRect();
        foreach (float fx in new[] { 0.15f, 0.5f, 0.85f })
        {
            Vector2 at = new(endRect.Position.X + endRect.Size.X * fx, endRect.GetCenter().Y);
            await Mouse(at, null);
            await Wait(0.2);
            Control? under = ((SceneTree)Engine.GetMainLoop()).Root.GuiGetHoveredControl();
            Note($"End Turn button {endRect} at x {fx:0.00}: under the mouse {under?.GetPath().ToString() ?? "nothing"}; teammate card hovered {HandViewer.HoveredId ?? "none"}");
        }
        await Mouse(away, null);
        await Wait(0.2);
        HandViewer.ToggleHideOwnHand();
        await Wait(0.4);
        await Snap("three stacked hands, your hand hidden");
        await Hover(1000, c => c.Type == CardType.Status, "front hand, hand hidden");
        await Hover(1002, c => c.Type == CardType.Attack, "top hand, hand hidden");
        HandViewer.ToggleHideOwnHand();

        // ---- Each hand on its own, odd cards hovered and dragged ----
        Settings.MaxHandViewers = 1;
        HandViewer.View(1000);
        await Wait(0.5);
        await Snap("P1000 alone: statuses, curses, Skewer (X), Blade Dance");
        await Hover(1000, c => c.Type == CardType.Status, "a status card");
        await Hover(1000, c => c.Type == CardType.Curse, "a curse");
        await Hover(1000, c => c is Skewer, "Skewer (X cost)");
        await Drag(1000, c => c.Type == CardType.Status, new Vector2(960f, 400f), "a status card into the play zone");
        await Drag(1000, c => c.Type == CardType.Curse, new Vector2(960f, 400f), "a curse into the play zone");
        await Drag(1000, c => c is Skewer, enemy, "Skewer at the enemy");
        HandViewer.View(1001);
        await Wait(0.5);
        await Snap("P1001 alone: 10 cards, 3 upgraded");
        await Hover(1001, c => c.IsUpgraded, "an upgraded card");
        HandViewer.View(1002);
        await Wait(0.5);
        await Snap("P1002 alone: afflicted cards");
        await Hover(1002, c => c.Affliction != null, "an afflicted card");
        await Drag(1002, c => c.Affliction != null && c.TargetType == TargetType.AnyEnemy, enemy, "an afflicted attack at the enemy");

        // ---- Pile screens ----
        foreach (var (id, pile) in new[] { (1000UL, PileType.Discard), (1000UL, PileType.Exhaust), (1001UL, PileType.Discard), (1001UL, PileType.Draw) })
        {
            int row = PileViewer.Teammates().FindIndex(t => t.player.NetId == id);
            PileViewer.OpenTeammatePile(row, pile);
            await Wait(0.6);
            Note($"P{id} {pile}: {PileViewer.DescribeOpenView()}");
            await Snap($"P{id} {pile} pile screen");
            PileViewer.OpenTeammatePile(row, pile);
            await Wait(0.4);
            if (NCapstoneContainer.Instance?.InUse == true) await PressKey(Key.Escape);
        }
        Settings.TextList = true;
        int row1000 = PileViewer.Teammates().FindIndex(t => t.player.NetId == 1000);
        PileViewer.OpenTeammatePile(row1000, PileType.Discard);
        await Wait(0.4);
        await Snap("text list mode (Show piles as text): P1000 discard");
        PileViewer.OpenTeammatePile(row1000, PileType.Discard);
        Settings.TextList = false;
        await Wait(0.3);

        // ---- 1000 in odd states (lock-step) ----
        HandViewer.View(1000);
        if (await Ask(1))
        {
            await Wait(0.8);
            Note("P1000 after Blade Dance: " + string.Join(" | ", HandViewer.DumpHandOf(1000, withGlow: true)));
            await Snap("P1000 after Blade Dance (generated Shivs)");
            await Hover(1000, c => c is Shiv, "a generated Shiv");
        }
        if (await Ask(2))
        {
            await Wait(0.8);
            Note($"P1000 empty hand: shown {HandViewer.DumpHandOf(1000, withGlow: false).Count}; {HandViewer.DescribeCards()}; strip {string.Join("/", PileViewer.StripCounts(1000) ?? Array.Empty<string>())}");
            await Snap("P1000 with an empty hand");
        }
        if (await Ask(3))
        {
            await Wait(1.5);
            Note($"P1000 dead: viewing {HandViewer.DescribeViewed()}; {HandViewer.DescribeCards()}; strip {string.Join("/", PileViewer.StripCounts(1000) ?? Array.Empty<string>())}; {PileViewer.DescribeState()}");
            await Snap("P1000 dead: their Pile Viewer and hand");
            await Hover(1000, _ => true, "a dead teammate's card");
        }
        Send(me, "gold 1"); // releases 1001/1002 (they wait for ExploreSteps + 1)
        await Wait(0.5);

        // ---- A multi-enemy fight ----
        Send(me, "win");
        await WaitUntil(() => !CombatManager.Instance.IsInProgress, 20);
        await Wait(1.5);
        Send(me, "fight " + ModelDb.AllEncounters.First(e => e is CultistsNormal).Id.Entry, inCombat: false);
        await WaitUntil(() => CombatManager.Instance.IsInProgress, 30);
        await Wait(2.5);
        var enemies = Combat.HittableEnemies.ToList();
        Note($"fight 2: {enemies.Count} enemies; teammates alive: {string.Join(", ", PileViewer.Teammates().Select(t => $"{t.player.NetId}={!t.player.Creature.IsDead}"))}; viewing {HandViewer.DescribeViewed()}");
        HandViewer.View(1001);
        await Wait(0.6);
        await Snap("fight 2 (Cultists): P1001's hand");
        foreach (Creature e in enemies)
        {
            Vector2 at = NCombatRoom.Instance!.GetCreatureNode(e)!.Hitbox.GetGlobalRect().GetCenter();
            await Drag(1001, c => c.TargetType == TargetType.AnyEnemy, at, $"an attack at {e.Name} (#{enemies.IndexOf(e) + 1})");
        }
        HandViewer.View(1000);
        await Wait(0.6);
        Note($"fight 2, P1000 (died last fight): alive {!t0.Creature.IsDead}; {HandViewer.DescribeCards()}");
        await Snap("fight 2: P1000 (died in fight 1)");
        if (!t0.Creature.IsDead && await Ask(4))
        {
            await Wait(2.5);
            Note("P1000 choosing (Survivor discard): " + string.Join(" | ", HandViewer.DumpHandOf(1000, withGlow: true)));
            await Snap("P1000 in a card choice (Survivor: discard a card)");
            await Hover(1000, _ => true, "a card while they choose");
            Vector2 cultist = NCombatRoom.Instance!.GetCreatureNode(Combat.HittableEnemies.First())!.Hitbox.GetGlobalRect().GetCenter();
            await Drag(1000, c => c.TargetType == TargetType.AnyEnemy, cultist, "an attack while they choose");
        }
    }

    // ---------------- Harness: a teammate without the Pile Viewer (test fixtures only) ----------------

    private const int HarnessSteps = 5;

    /// <summary>Host (modded): looks at and aims the fixtures-only teammate's cards; they report what they saw.</summary>
    private static async Task HarnessObserver()
    {
        Player me = Me;
        Player teammate = PileViewer.Teammates().First(t => t.player.NetId == MutatorId).player;
        _hostGold0 = me.Gold;
        _mutatorGold0 = teammate.Gold;
        Vector2 away = new(960f, 300f);
        Settings.CardSignals = Settings.CardSignalMode.SendAndReceive;
        HandViewer.View(MutatorId);
        await Wait(0.5);
        int shown = HandViewer.ShownCards().Count;
        Check("you see the hand of a teammate without the Pile Viewer", shown > 0 && shown == PileType.Hand.GetPile(teammate).Cards.Count, $"{shown} cards");
        int middle = HandViewer.MiddleIndexOf(_ => true);

        // 1-2: hovering inside the delay sends nothing (they watch the whole time).
        if (await Ask(1))
        {
            Settings.HoverDelay = 0.9f;
            await Mouse(HandViewer.PeekPoint(middle)!.Value, null);
            await Wait(0.5);
            Check("hovered within the delay: no closeup yet", HandViewer.HoveredId != null && HandViewer.CloseupId == null, HandViewer.DescribeHover());
            await Mouse(away, null);
            await Wait(0.3);
        }
        await Ask(2);

        // 3: an instant closeup: they see the card over your head.
        Settings.HoverDelay = 0f;
        await Mouse(HandViewer.PeekPoint(middle)!.Value, null);
        await Wait(0.2);
        Check("instant hover: closeup shows", HandViewer.CloseupId != null, HandViewer.DescribeHover());
        await Ask(3);
        await Mouse(away, null);
        Settings.HoverDelay = 0.1f;
        await Wait(0.3);

        // 4: aim one of their single-target cards; they see it, then play it out from under your aim.
        int target = HandViewer.IndexOf(c => c.TargetType == TargetType.AnyEnemy);
        if (Check("they have a single-target card", target >= 0))
        {
            Vector2 from = HandViewer.PeekPoint(target)!.Value, to = NCombatRoom.Instance!.GetCreatureNode(FirstEnemy)!.Hitbox.GetGlobalRect().GetCenter();
            await Mouse(from, null);
            await Mouse(from, true);
            for (int i = 1; i <= 6; i++) await Mouse(from.Lerp(to, i / 6f), null);
            await Wait(0.3);
            Check("aiming their card", HandViewer.IsAiming, HandViewer.DescribeDragCard());
            await Ask(4);
            await Wait(0.5);
            Check("they play the card you're aiming: your aim ends cleanly", !HandViewer.IsDragging && !HandViewer.IsAiming && !NTargetManager.Instance.IsInSelection,
                $"dragging={HandViewer.IsDragging} aiming={HandViewer.IsAiming} selection={NTargetManager.Instance.IsInSelection}");
            await Mouse(to, false);
            await Mouse(away, null);
            await Wait(0.3);
        }
        await Ask(5);
    }

    /// <summary>The fixtures-only teammate (no Pile Viewer): what the game shows it over the host's head.</summary>
    private static async Task HarnessTeammate()
    {
        Player me = Me;
        _hostGold0 = Host.Gold;
        var tracker = RunManager.Instance.HoveredModelTracker;
        var hand = PileType.Hand.GetPile(me);
        Check("this teammate runs only the test fixtures (no Pile Viewer)", Harness
            && NCombatRoom.Instance!.GetChildren().All(n => !n.Name.ToString().StartsWith("Spire2PileViewer")));
        for (int k = 1; k <= HarnessSteps; k++)
        {
            await WaitUntil(() => Host.Gold >= _hostGold0 + k, 60);
            if (Host.Gold < _hostGold0 + k)
            {
                Check($"host asked for step {k}", false);
                return;
            }
            switch (k)
            {
                case 1:
                    _hostHoverSeen.Clear();
                    _watchHostHover = true;
                    _ = TaskHelper.RunSafely(WatchHostHover());
                    break;
                case 2:
                    _watchHostHover = false;
                    Check("nothing over the host's head while their hover delay runs", _hostHoverSeen.Count == 0,
                        _hostHoverSeen.Count == 0 ? "nothing seen" : string.Join(",", _hostHoverSeen.Distinct()));
                    break;
                case 3:
                    await WaitUntil(() => tracker.GetHoveredModel(Host.NetId) is CardModel c && c.Owner == me, 3);
                    Check("once their closeup shows, you see your card over the host's head",
                        tracker.GetHoveredModel(Host.NetId) is CardModel seen && seen.Owner == me, $"{tracker.GetHoveredModel(Host.NetId)?.Id.Entry ?? "nothing"}");
                    Check("...without the gold (no Pile Viewer here)", !HandViewer.SignalGlowing(Host.NetId));
                    break;
                case 4:
                {
                    CardModel? aimed = null;
                    await WaitUntil(() => (aimed = tracker.GetHoveredModel(Host.NetId) as CardModel) != null && aimed.Owner == me && hand.Cards.Contains(aimed), 5);
                    if (!Check("you see the card the host is aiming, over their head", aimed != null && hand.Cards.Contains(aimed), aimed?.Id.Entry ?? "nothing")) break;
                    RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(aimed!, aimed!.TargetType == TargetType.AnyEnemy ? FirstEnemy : null));
                    await WaitUntil(() => !hand.Cards.Contains(aimed), 10);
                    await WaitUntil(() => tracker.GetHoveredModel(Host.NetId) != aimed, 5);
                    Check("...you play it: it leaves the host's head", !hand.Cards.Contains(aimed) && tracker.GetHoveredModel(Host.NetId) != aimed);
                    break;
                }
                case 5:
                    Check("still in combat, nothing over the host's head", CombatManager.Instance.IsInProgress && tracker.GetHoveredModel(Host.NetId) == null,
                        $"in combat {CombatManager.Instance.IsInProgress}, over their head: {tracker.GetHoveredModel(Host.NetId)?.Id.Entry ?? "nothing"}");
                    break;
            }
            int gold = me.Gold;
            Send(me, "gold 1");
            await WaitUntil(() => me.Gold > gold, 10);
        }
    }

    /// <summary>A modded third player: sees the fixtures-only teammate's card over the host's head, without gold.</summary>
    private static async Task HarnessBystander()
    {
        _hostGold0 = Host.Gold;
        var tracker = RunManager.Instance.HoveredModelTracker;
        bool saw = false, glowed = false;
        for (double t = 0; t < 90 && Host.Gold < _hostGold0 + HarnessSteps; t += 0.03)
        {
            if (tracker.GetHoveredModel(Host.NetId) is CardModel c && c.Owner?.NetId == MutatorId)
            {
                saw = true;
                glowed |= HandViewer.SignalGlowing(Host.NetId);
            }
            await Wait(0.03);
        }
        Check("a third player sees the teammate's card over the host's head", saw);
        Check("...without the gold: it isn't theirs", saw && !glowed);
    }

    private static async Task PressKey(Key key, bool shift = false, bool ctrl = false)
    {
        // Inject through Godot's input pipeline (same route as a physical key press), so this also checks
        // that the NGame._Input patch sees the key and nothing upstream swallows it.
        var down = new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = true };
        var up = new InputEventKey { Keycode = key, PhysicalKeycode = key, ShiftPressed = shift, CtrlPressed = ctrl, Pressed = false };
        Input.ParseInputEvent(down);
        await Wait(0.03);
        Input.ParseInputEvent(up);
        await Wait(0.1);
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
        await Wait(0.035); // ~2 frames: the game handles it on the next frame
    }

    private static async Task WaitUntil(Func<bool> condition, double timeoutSeconds)
    {
        for (double t = 0; t < timeoutSeconds && !condition(); t += 0.05) await Wait(0.05);
    }

    /// <summary>Every test fight: the Waterfall Giant (the user's favourite enemy).</summary>
    private static EncounterModel PickEncounter() => ModelDb.AllEncounters.First(e => e is WaterfallGiantBoss);

    private static async Task Wait(double seconds)
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        await tree.ToSignal(tree.CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private static void Log(string message) => ModLog.Info("AutoTest: " + message);
}
