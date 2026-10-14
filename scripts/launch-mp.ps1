# Launch a local multiplayer test: one host plus (Players - 1) clients.
# Applies test settings first (windowed, steam_appid.txt, dev build installed) via test-settings.ps1;
# run `test-settings.ps1 -Mode restore` afterwards to go back to normal play (Workshop subscription).
param(
    [int]$Players = 2,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraArgs = @(),
    # Run one side without any mods (-nomods), to check that a teammate without this mod is fine.
    [ValidateSet('', 'host', 'client')][string]$VanillaSide = '',
    # Send each instance's console output to %TEMP%\sts2_<n>.out instead of this terminal (automated runs).
    [switch]$QuietConsole,
    # Window size (always windowed), e.g. 3840x1600 to test at the monitor's resolution.
    [string]$WindowSize = '1280x720'
)
function Start-Game($gameArgs, $n) {
    if ($QuietConsole) {
        Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $gameArgs `
            -RedirectStandardOutput (Join-Path $env:TEMP "sts2_$n.out") -RedirectStandardError (Join-Path $env:TEMP "sts2_$n.err")
    }
    else {
        Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList $gameArgs
    }
}

$exe = Join-Path $GameDir 'SlayTheSpire2.exe'
# Windowed (1280x720 by default) + steam_appid.txt (restore later with: test-settings.ps1 -Mode restore).
& (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode apply -GameDir $GameDir -WindowSize $WindowSize
if (-not $?) { exit 1 }

$hostArgs = @('-fastmp', 'host_standard') + $ExtraArgs
if ($VanillaSide -eq 'host') { $hostArgs += '-nomods' }
Start-Game $hostArgs 'host'
Start-Sleep -Seconds 3
for ($i = 0; $i -lt $Players - 1; $i++) {
    $clientArgs = @('-fastmp', 'join', '-clientId', (1000 + $i)) + $ExtraArgs
    if ($VanillaSide -eq 'client' -and $i -eq 0) { $clientArgs += '-nomods' }
    Start-Game $clientArgs (1000 + $i)
}
