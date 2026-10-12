# Spire2 Pile Viewer

A Slay the Spire 2 mod for co-op: a small 3-slot strip (draw | exhaust | discard) hovers above each
teammate in combat, using the same icons as your own pile buttons, with live card counts. Click a slot
to view that teammate's pile.

## Usage

- **Click** a slot to open that teammate's pile in the game's own card-pile screen (click again to close).
- **Shift+drag** a strip to move all strips.
- **Hotkeys** (configurable): teammate 1 — Shift+Z draw, Shift+V exhaust, Shift+C discard; teammate 2 —
  Ctrl+…; teammate 3 — Ctrl+Shift+….
- **F1** opens the settings: show/hide, size, background opacity, show over your own character, text-list
  mode, hotkey rebinding (pick a teammate, click a binding, press keys), reset position / all.

Works for 2–4 players. See [PILE_VIEWER.md](PILE_VIEWER.md) for development notes.

## Building

Requires the .NET 9 SDK and a Slay the Spire 2 install (the project references the game's own
`sts2.dll`, `GodotSharp.dll` and `0Harmony.dll`; set `Sts2Dir` in `src/Spire2PileViewer.csproj` if
your install is elsewhere).

```
dotnet build src/Spire2PileViewer.csproj
```

Then copy `Spire2PileViewer.dll` and `Spire2PileViewer.json` into
`<game>/mods/Spire2PileViewer/` (or run `scripts/install-local.ps1`).

## Testing multiplayer locally

`scripts/launch-mp.ps1 -Players 2` starts a host and a client on one machine (`-fastmp`), per the
[ModTemplate-StS2 testing guide](https://github.com/Alchyr/ModTemplate-StS2/wiki/Testing-and-Debugging).
Add `-pileviewerself` to the game's launch arguments to also show a strip over your own character.

## Credits

- Architecture (Harmony lifecycle hooks, overhead-label placement above creatures, per-instance log
  files, layout persistence approach) is adapted from **BetterSpire2** by **jdr**
  ([Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3747530215),
  [source](https://github.com/AlecRichardson/BetterSpire2)).
- Local multiplayer testing approach from Alchyr's
  [ModTemplate-StS2](https://github.com/Alchyr/ModTemplate-StS2).
