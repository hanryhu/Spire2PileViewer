# Stage README screenshots: install the local build, then launch a 3-player game in showcase mode.
# The first client stages 6 shots (strips with a teammate's frame lit, F1 menu, a teammate's pile screen, their
# hand behind yours, your hand hidden with a closeup of theirs, aiming one of their cards) and the game saves each
# itself to %APPDATA%\SlayTheSpire2\showcase\shot_N.png (1280x720; no window capture, so focus doesn't matter).
# Copy them to assets/ (README) and workshop/previews/ (960x540, each < 1 MB). Disable other UI mods first.
param(
    [int]$Players = 3,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Error 'Close the game first (the mod DLL is locked while it runs).'
    exit 1
}
& (Join-Path $PSScriptRoot 'launch-mp.ps1') -Players $Players -GameDir $GameDir `
    -ExtraArgs '-pileviewerautotest', '-pileviewershowcase', '-pileviewerplayers', $Players
