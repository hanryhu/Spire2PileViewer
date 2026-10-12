# Switch the game between the player's normal settings and test settings (game must be closed — it rewrites
# settings.save on exit).
#   apply   : back up settings.save, then windowed 1280x720 centered (fullscreen breaks multi-window
#             testing), and create steam_appid.txt so -fastmp instances launch without Steam.
#   restore : put fullscreen / window size / position back from the backup (other settings such as
#             keybindings are left as they are now), delete steam_appid.txt and the backup.
param(
    [Parameter(Mandatory)][ValidateSet('apply', 'restore')][string]$Mode,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$ProfileDir = (Join-Path $env:APPDATA 'SlayTheSpire2\steam\76561198173012365')
)

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Error 'Close the game first (it rewrites settings.save on exit).'
    exit 1
}

$settings = Join-Path $ProfileDir 'settings.save'
$backup = "$settings.pre-test"
$appId = Join-Path $GameDir 'steam_appid.txt'
$display = 'fullscreen', 'window_size', 'window_position'

if ($Mode -eq 'apply') {
    if (-not (Test-Path $backup)) { Copy-Item $settings $backup }
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    $json.fullscreen = $false
    $json.window_size = [pscustomobject]@{ X = 1280; Y = 720 }
    $json.window_position = [pscustomobject]@{ X = -1; Y = -1 }
    [System.IO.File]::WriteAllText($settings, ($json | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
    if (-not (Test-Path $appId)) { Set-Content -Path $appId -Value 2868840 -Encoding ascii }
    'Test settings applied (windowed 1280x720, steam_appid.txt).'
}
else {
    if (Test-Path $backup) {
        $json = Get-Content $settings -Raw | ConvertFrom-Json
        $saved = Get-Content $backup -Raw | ConvertFrom-Json
        foreach ($key in $display) { $json.$key = $saved.$key }
        [System.IO.File]::WriteAllText($settings, ($json | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
        Remove-Item $backup
    }
    if (Test-Path $appId) { Remove-Item $appId }
    'Normal settings restored.'
}
