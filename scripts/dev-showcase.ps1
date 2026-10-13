# Stage README screenshots: install the local build, then launch a 3-player game in showcase mode.
# The first client logs "SHOT 1..6" (default view, F1 menu, a teammate's pile screen, their hand behind yours,
# your hand hidden with a closeup of theirs, aiming one of their cards) and holds each a few
# seconds; capture that client window at each. Disable other UI mods first for clean shots.
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
