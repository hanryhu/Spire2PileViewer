# Pile Viewer — working notes

Living notes so work can resume after an interruption. Newest status first.

## Status

| Milestone | Scope | State | Tag |
|---|---|---|---|
| 1 | Buttons UI: 3-slot strip (draw / exhaust / discard) above each teammate, native icons + count badges | done | `m1` |
| 2 | Load correct info: clicking a slot toggles a text popup above the strip listing that pile (grouped, name-sorted, live-updating) | done | `m2` |
| 3 | Click opens the native `NCardPileScreen` on the teammate's pile (owner caption, toggle, empty → thought bubble, text popup as fallback); strips hide under overlays; hover bump. Verified in a real 2-player game (host + client) | done | `m3` |
| 4 | Convenience: draggable strip (persisted offset), settings | — | `m4` |
| 5 | Hotkeys + 3–4 players | — | `m5` |

Each milestone is one or more commits ending in an annotated tag `mN`, so testing can roll back with
`git checkout mN` and rebuild.

## Dev loop

1. `dotnet build src/Spire2PileViewer.csproj` (uses `%LOCALAPPDATA%\dotnet9`, `DOTNET_ROOT` set).
2. Game closed → `scripts/dev-autotest.ps1`: installs to `<game>\mods\Spire2PileViewer\` and launches a solo
   game with `-pileviewerautotest`.
3. `AutoTest` (inert without the flag) starts an unsaved Ironclad run from the main menu, jumps into a weak
   monster fight via `RunManager.EnterRoomDebug`, logs `PileViewer.DescribeState()`, simulates a press on
   each slot, then logs `AutoTest: done`.
4. Check `%APPDATA%\SlayTheSpire2\spire2pileviewer_log.txt` and take a window screenshot.
5. Multiplayer (the real target — always verify here): game closed → `scripts/dev-autotest-mp.ps1`.
   Host + client launch with `-fastmp` + `-pileviewerautotest`; in the lobby each picks a character
   (host Ironclad, client Silent) via `StartRunLobby.SetLocalCharacter` + `SetReady(true)`; the host enqueues
   a networked `fight <encounter>` (`ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, cmd,
   false))`) so both enter combat; each instance presses its teammate's slots and logs
   `PileViewer.DescribeOpenView()`. Per-instance logs: `spire2pileviewer_host_log.txt`,
   `spire2pileviewer_1000_log.txt`. Screenshot both windows (scratchpad `waitmp.sh`).
6. Manual: `scripts/launch-mp.ps1 -Players 2` (no autotest) and click through.

Flags: `-pileviewerself` shows a strip over the local player too; `-pileviewerautotest` implies it.

## Game facts (sts2 v0.111.0, decompiled at `source\repos\sts2-decompiled\v0.111.0`)

- Piles: `PileType.Draw/Discard/Exhaust.GetPile(player)` → `CardPile` (`Cards`, `IsEmpty`,
  `ContentsChanged`, `CardAddFinished`, `CardRemoveFinished`). Works for any player — every client simulates
  all players, so teammates' piles are available locally (same as BetterSpire2's hand viewer).
- Players in combat: `CombatManager.Instance.DebugOnlyGetState()?.Players`; local id
  `RunManager.Instance.NetService.NetId` (solo run: player NetId 1). `CombatManager.Instance.IsInProgress`.
- Native pile buttons: `NCombatRoom.Instance.Ui.{DrawPile,DiscardPile,ExhaustPile}` (`NCombatCardPile : NButton`).
  Node tree: `Icon` (TextureRect 80×80, `res://images/packed/combat_ui/{draw,discard,exhaust}_pile.png`),
  `CountContainer/Background` (`pile_button_count.png`, 48×48) + `CountContainer/Count` (MegaLabel),
  `HotkeyIcon`. Duplicating `Icon` rendered blank → build a fresh TextureRect from its texture.
- Native click → `NCardPileScreen.ShowScreen(CardPile pile, string[] closeHotkeys)`: generic over any pile,
  opens through `NCapstoneContainer` (closes if the same pile is already open:
  `NCapstoneContainer.Instance.CurrentCapstoneScreen is NCardPileScreen s && s.Pile == pile`).
  Empty pile → native shows a thought bubble (`NThoughtBubbleVfx.Create(text, creature, 2.0)`).
  Draw pile is shown sorted by rarity/id (hides order) — same for teammates.
- Creature placement: `NCombatRoom.Instance.GetCreatureNode(player.Creature).GlobalPosition`; BetterSpire2's
  damage label is at +(10, −320). Strip bottom-center at +(0, −350) clears it.
- Mod loading: `<game>\mods\<id>\<id>.dll` + `<id>.json`; entry `[ModInitializer("Init")]`.
- Hotkeys: native uses `MegaInput.viewDrawPile` etc. via `NHotkeyManager`.
- Autotest-friendly APIs: `NGame.Instance.StartNewSingleplayerRun(...)`, `RunManager.Instance.EnterRoomDebug(...)`,
  `ModelDb.AllCharacters`, `ModelDb.AllEncounters` (`RoomType`, `IsWeak`). `autoslay` is disabled in release.

## Mod architecture

- `ModEntry` — Harmony patches from `Patches.All`.
- `Patches` — combat lifecycle: `CombatManager.SetUpCombat` (hook frame updates), `EndCombatInternal`,
  `LoseCombat`, `NGame.ReturnToMainMenu` (hide).
- `PileViewer` — strips. Updated from `SceneTree.ProcessFrame` (mod-defined Godot subclasses are not
  registered scripts, so `_Process`/`_Input` overrides never run; use signals/events).
  Strips are children of `NCombatRoom` (like BetterSpire2's labels), `ZIndex = 100`, slots are Godot `Button`s.
- `AutoTest` — dev harness above.
- `ModLog` — per-instance log file.

## Decisions (made autonomously; revisit if wrong)

- Slot order is draw | exhaust | discard as specified (native layout has exhaust above discard).
- Strips show for teammates only (not yourself) unless `-pileviewerself`.
- Click target: whole slot. Hover tooltip = pile name (Godot tooltip) + native-style 1.25x icon bump.
- Click uses the native pile screen (same as your own piles) with the teammate's character name prepended
  to the bottom caption in gold. Draw pile stays rarity-sorted like native (no order leak).
- Empty exhaust pile: no native message exists (`OPEN_EMPTY_EXHAUST` missing) → English "No exhausted cards."
- Strips hide while any capstone overlay is open (they rendered on top of the pile screen otherwise).

## Gotchas

- Bash heredocs turn `"\\n"` inside Python string literals into real newlines → use the Edit tool for C#
  strings containing `\n`.
- The game must be closed before installing (DLL locked).
