# Dev loop, multiplayer: install the local build, then launch a host and (Players - 1) clients with
# -pileviewerautotest. The lobby readies up by itself, the host sends a networked "fight", and each
# instance exercises its teammates' strips. Logs: %APPDATA%\SlayTheSpire2\spire2pileviewer_{host,1000,...}_log.txt
param(
    [int]$Players = 2,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Error 'Close the game first (the mod DLL is locked while it runs).'
    exit 1
}
& (Join-Path $PSScriptRoot 'install-local.ps1') -GameDir $GameDir
& (Join-Path $PSScriptRoot 'launch-mp.ps1') -Players $Players -GameDir $GameDir -ExtraArgs '-pileviewerautotest'
