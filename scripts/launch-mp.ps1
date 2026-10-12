# Launch a local multiplayer test: one host plus (Players - 1) clients.
# Applies test settings first (windowed, steam_appid.txt) via test-settings.ps1.
param(
    [int]$Players = 2,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraArgs = @()
)

$exe = Join-Path $GameDir 'SlayTheSpire2.exe'
# Windowed 1280x720 + steam_appid.txt (restore later with: test-settings.ps1 -Mode restore).
& (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode apply -GameDir $GameDir
if (-not $?) { exit 1 }

Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (@('-fastmp', 'host_standard') + $ExtraArgs)
Start-Sleep -Seconds 3
for ($i = 0; $i -lt $Players - 1; $i++) {
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (@('-fastmp', 'join', '-clientId', (1000 + $i)) + $ExtraArgs)
}
