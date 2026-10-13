# Launch a local multiplayer test: one host plus (Players - 1) clients.
# Applies test settings first (windowed, steam_appid.txt) via test-settings.ps1.
param(
    [int]$Players = 2,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraArgs = @(),
    # Run one side without any mods (-nomods), to check that a teammate without this mod is fine.
    [ValidateSet('', 'host', 'client')][string]$VanillaSide = ''
)

$exe = Join-Path $GameDir 'SlayTheSpire2.exe'
# Windowed 1280x720 + steam_appid.txt (restore later with: test-settings.ps1 -Mode restore).
& (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode apply -GameDir $GameDir
if (-not $?) { exit 1 }

$hostArgs = @('-fastmp', 'host_standard') + $ExtraArgs
if ($VanillaSide -eq 'host') { $hostArgs += '-nomods' }
Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $hostArgs
Start-Sleep -Seconds 3
for ($i = 0; $i -lt $Players - 1; $i++) {
    $clientArgs = @('-fastmp', 'join', '-clientId', (1000 + $i)) + $ExtraArgs
    if ($VanillaSide -eq 'client' -and $i -eq 0) { $clientArgs += '-nomods' }
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $clientArgs
}
