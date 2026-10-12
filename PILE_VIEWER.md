# Pile Viewer — working notes

Living notes so work can resume after an interruption. Newest status first.

## Status

| Milestone | Scope | State | Tag |
|---|---|---|---|
| 1 | Buttons UI: 3-slot strip (draw / exhaust / discard) above each teammate, native icons + count badges | done | `m1` |
| 2 | Load correct info: clicking a slot toggles a text popup above the strip listing that pile (grouped, name-sorted, live-updating) | done | `m2` |
| 3 | Click opens the native `NCardPileScreen` on the teammate's pile (owner caption, toggle, empty → thought bubble, text popup as fallback); strips hide under overlays; hover bump. Verified in a real 2-player game (host + client) | done | `m3` |
| 4 | Shift+drag moves all strips (saved offset); F1 settings menu (on/off, size 50–200%, background opacity, show over self, text-list mode, reset position, reset all); native-style count bump; verified live count updates over the network | done | `m4` |
| 5 | Configurable hotkeys: F1 menu dropdown of the actual teammates (P-number + character), click a binding then press a combo (Esc cancels, Backspace clears), conflicts shown in red and block saving; defaults Shift/Ctrl/Ctrl+Shift + Z/V/C per teammate. Verified with 3 players | done | `m5` |
| 6 | Revisions: hotkeys rank below the game (run after `NHotkeyManager._UnhandledInput`, skip keys the game maps, console open, or text editing); count badges copied per pile from the native buttons (exhaust = centered purple number); toggle renamed "Show teammates' piles"; conflicts with teammate slots not in this game are warnings (saved; lowest teammate wins); pile-screen captions name the teammate ("…into The Ironclad's draw pile…", "…start of The Ironclad's turn", "…exhausted by The Ironclad…"; English only); README screenshots via `scripts/dev-showcase.ps1` | done | `m6` |
| 7 | Fix: a key moved off the game's mouse+keyboard map still blocked our hotkey because the keyboard-only map was also checked; now only the active map counts (per `NControllerManager.InputType`, like the game) | done | `m7` |

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

Flags: `-pileviewerself` shows a strip over the local player too; `-pileviewerautotest` implies it in solo;
`-pileviewerplayers N`; `-pileviewershowcase` stages README screenshots (`scripts/dev-showcase.ps1`; the first
client logs SHOT 1/2/3 — disable BetterSpire2 in settings.save mod_list first for clean shots).

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
- `Settings` / `SettingsMenu` — persisted options and the F1 panel (also hosts the input patch).
- `Hotkeys` — `KeyCombo`, active vs draft bindings, conflict detection.
- `AutoTest` — dev harness above (`-pileviewerplayers N` for 3–4 player runs).
- `ModLog` — per-instance log file.

## Verified behaviour (2-player auto-test, 2026-10-11)

- Teammate's piles update live: client runs `draw 2`, `card X Discard`, `card X Exhaust` (networked) → host's
  strip shows 5→3, 0→1, 0→1 within ~1s each.
- Native pile screen opens on the teammate's pile on both host and client; second click toggles closed;
  empty pile → thought bubble; strips hidden while the screen is open.
- F1 menu controls apply immediately (scale, opacity, on/off, reset position) and persist.
- Auto-test uses `spire2pileviewer_settings_autotest.json`, never the real settings file.
- Window screenshots only work when the user isn't using the PC (Windows blocks focus stealing) — rely on logs.

- 3-player run: every instance shows 2 strips; dropdown lists exactly the 2 teammates; Shift+Z / Ctrl+Z open
  teammate 1 / 2 draw piles; rebinding into a conflict is refused (status red, active binding unchanged) and
  resolving it saves; keys injected with `Input.ParseInputEvent` (real input pipeline) work the same.

- Manual test case (user, m6): **The Ball** — after it's played it goes into the draw pile at a random position
  instead of the discard pile; the strip's counts followed correctly.
- m7 regression: AutoTest moves the game's D action to P in the in-memory `_mKbInputMap` (not
  `ModifyMKbKey`, which would save the player's keybindings), binds Shift+D, expects no warning and the
  teammate's draw pile to open, then restores D.

## Decisions (made autonomously; revisit if wrong)

- Slot order is draw | exhaust | discard as specified (native layout has exhaust above discard).
- Strips show for teammates only (not yourself) unless `-pileviewerself`.
- Click target: whole slot. Hover tooltip = pile name (Godot tooltip) + native-style 1.25x icon bump.
- Click uses the native pile screen (same as your own piles) with the teammate's character name prepended
  to the bottom caption in gold. Draw pile stays rarity-sorted like native (no order leak).
- Empty exhaust pile: no native message exists (`OPEN_EMPTY_EXHAUST` missing) → English "No exhausted cards."
- Strips hide while any capstone overlay is open (they rendered on top of the pile screen otherwise).
- F1 is shared with BetterSpire2's menu: both open together; ours docks top-right, theirs is centered.
- One shared drag offset for all strips (relative to each character), not per player.
- Count bump animation only when a count grows (matches native AddCard; RemoveCard has no animation).
- Hotkeys bind per teammate *position* (teammate 1 = first other player in party order), not per Steam
  account, so they keep working with different friends. Party order is the same on every client.
- Default hotkeys use Z/V/C (unused by the game): teammate 1 Shift+, 2 Ctrl+, 3 Ctrl+Shift+. Reserved:
  F1, F3 (BetterSpire2 hand viewer), Esc. Unsaved conflicting edits are discarded when the menu closes.
- Key capture (rebinding) and F1 use a Harmony prefix on `NGame._Input`. Pile hotkeys use a postfix on
  `NHotkeyManager._UnhandledInput` so they rank *below* the game: only unhandled keys, never while the dev
  console is open or a text field is edited, never for a key in the game's *active* key map (`_fKbInputMap` in keyboard-only mode, else `_mKbInputMap`)
  (the game matches its hotkeys on the key alone, ignoring modifiers — Shift+S still fires S).
- Conflicts: duplicates between two editable rows (teammates in this game) and reserved keys block saving;
  duplicates with absent-teammate rows and game-key overlaps are warnings (saved; lowest teammate wins).
- Native count badges differ per pile: draw/discard have a red `CountContainer/Background` badge in a corner;
  exhaust has no Background and centers a pink number with dark-purple outline over the icon. We copy
  geometry/texture/colors/outline/font size from the native nodes, scaled 56/80.
- The native buttons count only animated card arrivals (`CardAddFinished`), so console-added cards don't move
  your own native counts; strips read `CardPile.Cards.Count` directly.

## Gotchas

- Bash heredocs turn `"\\n"` inside Python string literals into real newlines → use the Edit tool for C#
  strings containing `\n`.
- The game must be closed before installing (DLL locked).
