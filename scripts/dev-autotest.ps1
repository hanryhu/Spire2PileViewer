# Dev loop: install the local build, then launch a solo game with -pileviewerautotest
# (starts an unsaved run, jumps into a fight, exercises the strip). Watch
# %APPDATA%\SlayTheSpire2\spire2pileviewer_log.txt for "AutoTest: done".
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraArgs = @()
)

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Error 'Close the game first (the mod DLL is locked while it runs).'
    exit 1
}
& (Join-Path $PSScriptRoot 'install-local.ps1') -GameDir $GameDir

& (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode apply -GameDir $GameDir
if (-not $?) { exit 1 }
Start-Process -FilePath (Join-Path $GameDir 'SlayTheSpire2.exe') -WorkingDirectory $GameDir -ArgumentList (@('-pileviewerautotest') + $ExtraArgs)
