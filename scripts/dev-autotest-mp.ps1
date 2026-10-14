# Automated multiplayer test: build + install the mod, launch a host and (Players - 1) clients with
# -pileviewerautotest, wait for every instance to finish, print the checks and a summary, close the
# instances and restore normal settings. Exit code 1 if anything failed.
#   -Suite fast (default, ~30 s): every feature once.
#   -Suite full (up to 3 min per pass, before pushes): + cross-instance accuracy/glow, regressions, a second
#          fight; runs twice: windowed 1280x720, then windowed at the primary monitor's resolution.
#   -Suite mixed -VanillaSide host|client: one side without mods; reach the fight by hand, then it runs.
#   -WindowSizes 1280x720,2560x1440: override the window size(s) to run at (one pass each, always windowed).
# Logs: %APPDATA%\SlayTheSpire2\spire2pileviewer_{host,1000,...}_log.txt (the last pass's)
param(
    [int]$Players = 2,
    [ValidateSet('fast', 'full', 'mixed')][string]$Suite = 'fast',
    [ValidateSet('', 'host', 'client')][string]$VanillaSide = '',
    [int]$TimeoutSeconds = 0,
    [string[]]$WindowSizes = @(),
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2'
)

if (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue) {
    Write-Error 'Close the game first (the mod DLL is locked while it runs).'
    exit 1
}
$runStarted = Get-Date
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $env:LOCALAPPDATA 'dotnet9\dotnet.exe'
if (Test-Path $dotnet) {
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    & $dotnet build (Join-Path $repo 'src\Spire2PileViewer.csproj') -c Debug -nologo -v:q -tl:off
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host "No .NET 9 SDK at $dotnet - testing the existing build in src\bin\Debug." -ForegroundColor Yellow
}

if ($WindowSizes.Count -eq 0) {
    $WindowSizes = @('1280x720')
    if ($Suite -eq 'full') {
        # The primary monitor's resolution in physical pixels (not DPI-scaled).
        $gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution } | Select-Object -First 1
        if ($gpu) { $WindowSizes += "$($gpu.CurrentHorizontalResolution)x$($gpu.CurrentVerticalResolution)" }
        $WindowSizes = @($WindowSizes | Select-Object -Unique)
    }
}
if ($Suite -eq 'mixed') { $WindowSizes = @($WindowSizes[0]) } # played by hand: one pass

$logDir = Join-Path $env:APPDATA 'SlayTheSpire2'
$names = @('host') + (0..($Players - 2) | ForEach-Object { "$(1000 + $_)" })
if ($VanillaSide -eq 'host') { $names = $names | Where-Object { $_ -ne 'host' } }
if ($VanillaSide -eq 'client') { $names = $names | Where-Object { $_ -ne '1000' } }
$logs = $names | ForEach-Object { Join-Path $logDir "spire2pileviewer_$($_)_log.txt" }
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = @{ fast = 120; full = 300; mixed = 900 }[$Suite] }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$failed = 0

foreach ($size in $WindowSizes) {
    if ($WindowSizes.Count -gt 1) { Write-Host "=== windowed $size ===" -ForegroundColor Cyan }
    $started = Get-Date
    # launch-mp applies the test settings, which installs the dev build; the restore at the end removes it.
    & (Join-Path $PSScriptRoot 'launch-mp.ps1') -Players $Players -GameDir $GameDir -VanillaSide $VanillaSide -QuietConsole `
        -WindowSize $size -ExtraArgs '-pileviewerautotest', '-pileviewerplayers', $Players, '-autotestsuite', $Suite

    # Wait for every modded instance to log "AutoTest: done".
    $deadline = $started.AddSeconds($TimeoutSeconds)
    function Done($log) { (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started -and (Select-String -Path $log -Pattern 'AutoTest: done' -Quiet -Encoding utf8) }
    while ((Get-Date) -lt $deadline -and ($logs | Where-Object { -not (Done $_) })) { Start-Sleep -Milliseconds 500 }

    # Report: every check, then the cross-instance dumps (REFn on a client == VIEWn on the host, as sets).
    foreach ($i in 0..($names.Count - 1)) {
        if (-not (Test-Path $logs[$i])) { continue }
        $lines = Get-Content $logs[$i] -Encoding UTF8
        foreach ($line in $lines | Where-Object { $_ -match 'AutoTest: (CHECK|RESULT)' }) {
            $text = $line -replace '^\[[\d:]+\] AutoTest: ', "[$($names[$i])] "
            if ($text -match 'CHECK FAIL') { $failed++; Write-Host $text -ForegroundColor Red }
            elseif ($text -match 'RESULT') { Write-Host $text -ForegroundColor Cyan }
            else { Write-Host $text }
        }
        if (-not (Done $logs[$i])) { $failed++; Write-Host "[$($names[$i])] did not finish within ${TimeoutSeconds}s" -ForegroundColor Red }
    }
    function Dump($name, $tag) {
        $log = Join-Path $logDir "spire2pileviewer_$($name)_log.txt"
        if (-not (Test-Path $log) -or (Get-Item $log).LastWriteTime -lt $started) { return @() } # only this pass's logs
        @(Get-Content $log -Encoding UTF8 | Where-Object { $_ -match "AutoTest: $tag " } | ForEach-Object { ($_ -split "AutoTest: $tag ", 2)[1] } |
            ForEach-Object { if ($tag -eq 'REF2' -or $tag -eq 'VIEW2') { $_ -split ', ' } else { $_ } } | Sort-Object)
    }
    foreach ($pair in @(@('1', '1000'), @('2', '1000'), @('3', '1001'))) {
        $ref = Dump $pair[1] "REF$($pair[0])"
        $view = Dump 'host' "VIEW$($pair[0])"
        if ($ref.Count -eq 0 -and $view.Count -eq 0) { continue }
        $same = ($ref.Count -eq $view.Count) -and -not (Compare-Object $ref $view)
        if ($same) { Write-Host "[runner] CHECK PASS teammate's own screen == host's view (dump $($pair[0]), $($ref.Count) items)" }
        else {
            $failed++
            Write-Host "[runner] CHECK FAIL teammate's own screen != host's view (dump $($pair[0]))" -ForegroundColor Red
            Compare-Object @($ref) @($view) | ForEach-Object { Write-Host "    $($_.SideIndicator) $($_.InputObject)" -ForegroundColor Red }
        }
    }

    if ($Suite -ne 'mixed') {
        Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

if ($Suite -ne 'mixed') { & (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode restore -GameDir $GameDir | Out-Null }
$elapsed = ((Get-Date) - $runStarted).TotalSeconds
$color = if ($failed -eq 0) { 'Green' } else { 'Red' }
$where = if ($WindowSizes.Count -gt 1) { " (windowed $($WindowSizes -join ', '))" } else { '' }
Write-Host ("{0} suite{1}: {2} in {3:0}s" -f $Suite, $where, $(if ($failed -eq 0) { 'ALL PASSED' } else { "$failed FAILED" }), $elapsed) -ForegroundColor $color
exit $(if ($failed -eq 0) { 0 } else { 1 })
