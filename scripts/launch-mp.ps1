# Launch a local multiplayer test: one host plus (Players - 1) clients.
# Requires steam_appid.txt (2868840) in the game folder so the game can start without Steam.
param(
    [int]$Players = 2,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string[]]$ExtraArgs = @()
)

$exe = Join-Path $GameDir 'SlayTheSpire2.exe'
$appId = Join-Path $GameDir 'steam_appid.txt'
if (-not (Test-Path $appId)) {
    Set-Content -Path $appId -Value 2868840 -Encoding ascii
}

Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (@('-fastmp', 'host_standard') + $ExtraArgs)
Start-Sleep -Seconds 3
for ($i = 0; $i -lt $Players - 1; $i++) {
    Start-Process -FilePath $exe -WorkingDirectory $GameDir -ArgumentList (@('-fastmp', 'join', '-clientId', (1000 + $i)) + $ExtraArgs)
}
