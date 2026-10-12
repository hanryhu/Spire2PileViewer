# Spire2 Pile Viewer

Slay the Spire 2 co-op mod: see your teammates' draw, exhaust, and discard piles, and their hand.

![Strips above each teammate](assets/strips.png)

- A strip with live pile counts hovers above each teammate during combat.
- **Click** a slot to open that pile in the game's own card-pile screen (click again to close).
- **Shift+drag** a strip to move it (each teammate's strip remembers its own spot).
- **Hotkeys** (rebindable): teammate 1 Shift+Z / Shift+V / Shift+C (draw / exhaust / discard), teammate 2
  Ctrl+…, teammate 3 Ctrl+Shift+…. The game's own hotkeys, the dev console, and text fields take precedence.
- Works with 2–4 players.

![Teammate's discard pile](assets/pile-screen.png)

## Hand Viewer

- Click the **eye** in a teammate's strip to show their hand *behind* yours, in shadow, using the game's own cards
  (live costs and numbers from their Strength, Weak, enchantments, Snecko Eye, …). Cards glow when *they* could
  play them (their energy; no glow once they've ended their turn). One teammate at a time.
- **Hover** one of their cards for the game's normal closeup.
- **Hide hand** (button above your energy; drag to move) hides your own hand so theirs is easy to read.
- Optional hotkeys: per-teammate Hand toggle and Hide/show my hand (unbound by default; set them in F1).

![Teammate's hand behind yours](assets/hand-viewer.png)

![Your hand hidden, closeup of a teammate card](assets/hide-hand.png)

## Settings (F1)

Two tabs. **Pile Viewer**: show/hide, size, background opacity, a strip over your own character, text-list mode, hotkey rebinding
(pick a teammate, click a binding, press keys; conflicts are flagged), reset strip positions / all. **Hand Viewer**: enable it, the Hide hand button,
teammate hand shadow (normal and while your hand is hidden), % of card visible, Hide/show my hand hotkey.

![F1 settings menu](assets/settings-menu.png)

## Install

Subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3818266065), or copy `Spire2PileViewer.dll` and `Spire2PileViewer.json` into `<Slay the Spire 2>/mods/Spire2PileViewer/`.

## Build

Needs the .NET 9 SDK and the game installed (set `Sts2Dir` in `src/Spire2PileViewer.csproj` if it isn't in
the default Steam folder): `dotnet build src/Spire2PileViewer.csproj`, then `scripts/install-local.ps1`.
Publish: `scripts/prepare-workshop.ps1`, then MegaCrit's
[ModUploader](https://github.com/megacrit/sts2-mod-uploader) `upload -w workshop` (Steam running).
Development notes: [PILE_VIEWER.md](PILE_VIEWER.md).

## Credits

Architecture adapted from **BetterSpire2** by **jdr**
([Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3747530215),
[source](https://github.com/AlecRichardson/BetterSpire2)). Local multiplayer testing via Alchyr's
[ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2).
