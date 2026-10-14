# Automated multiplayer test: build + install the mod, launch a host and clients with -pileviewerautotest, wait
# for every instance to finish, print the checks and a summary, close the instances and restore normal settings.
# Exit code 1 if anything failed.
#   -Suite fast (default, < 30 s): every feature once; windowed 1280x720, 2 players.
#   -Suite full (< 90 s, before pushes): two passes —
#        1. the full-only checks (cross-instance accuracy/glow, card signals, stacked hands, regressions, a second
#           fight) at 1280x720 with 3 players;
#        2. the fast checks again at the primary monitor's resolution (layout at another size), 2 players.
#      -SkipFast drops pass 2 (when you've just run -Suite fast).
#   -Suite harness: host + a teammate running only the test fixtures (-pileviewerharness: no Pile Viewer, like a
#      teammate without the mod, but scriptable) + a modded teammate. Card signals across that boundary.
#      Imperfect stand-in for vanilla (same mod list, so handshake/BaseLib behaviour isn't covered — use mixed).
#   -Suite mixed -VanillaSide host|client: one side without mods; reach the fight by hand, then it runs.
#   -WindowSizes 1280x720,2560x1440: override the window size of each pass, in order (always windowed).
# Logs: %APPDATA%\SlayTheSpire2\spire2pileviewer_{host,1000,...}_log.txt (the last pass's)
param(
    [int]$Players = 0,
    [ValidateSet('fast', 'full', 'harness', 'mixed')][string]$Suite = 'fast',
    [ValidateSet('', 'host', 'client')][string]$VanillaSide = '',
    [switch]$SkipFast,
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

function Monitor() {
    # The primary monitor's resolution in physical pixels (not DPI-scaled).
    $gpu = Get-CimInstance Win32_VideoController | Where-Object { $_.CurrentHorizontalResolution } | Select-Object -First 1
    if ($gpu) { "$($gpu.CurrentHorizontalResolution)x$($gpu.CurrentVerticalResolution)" } else { '1920x1080' }
}
function Pass($size, $players, $suiteArgs, $timeout, [switch]$Harness) {
    @{ Size = $size; Players = $players; Args = $suiteArgs; Timeout = $timeout; Harness = [bool]$Harness }
}
$passes = @(switch ($Suite) {
    'fast' { Pass '1280x720' ([Math]::Max($Players, 2)) @('-autotestsuite', 'fast') 90 }
    'full' {
        $list = @(Pass '1280x720' ([Math]::Max($Players, 3)) @('-autotestsuite', 'full', '-autotestskipfast') 150)
        if (-not $SkipFast) { $list += Pass (Monitor) 2 @('-autotestsuite', 'fast') 90 }
        $list
    }
    'harness' { Pass '1280x720' 3 @('-autotestsuite', 'harness') 150 -Harness }
    'mixed' { Pass '1280x720' ([Math]::Max($Players, 2)) @('-autotestsuite', 'mixed') 900 }
})
for ($i = 0; $i -lt [Math]::Min($WindowSizes.Count, $passes.Count); $i++) { $passes[$i].Size = $WindowSizes[$i] }

$logDir = Join-Path $env:APPDATA 'SlayTheSpire2'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$failed = 0

foreach ($pass in $passes) {
    $n = $pass.Players
    $label = "$($pass.Args -join ' ') - windowed $($pass.Size), $n players"
    if ($passes.Count -gt 1 -or $Suite -eq 'harness') { Write-Host "=== $label ===" -ForegroundColor Cyan }
    $started = Get-Date
    $names = @('host') + (0..($n - 2) | ForEach-Object { "$(1000 + $_)" })
    if ($VanillaSide -eq 'host') { $names = $names | Where-Object { $_ -ne 'host' } }
    if ($VanillaSide -eq 'client') { $names = $names | Where-Object { $_ -ne '1000' } }
    $logs = $names | ForEach-Object { Join-Path $logDir "spire2pileviewer_$($_)_log.txt" }
    $timeout = if ($TimeoutSeconds -gt 0) { $TimeoutSeconds } else { $pass.Timeout }

    # launch-mp applies the test settings, which installs the dev build; the restore at the end removes it.
    $extra = @('-pileviewerautotest', '-pileviewerplayers', $n) + $pass.Args
    $harness = [bool]$pass.Harness
    & (Join-Path $PSScriptRoot 'launch-mp.ps1') -Players $n -GameDir $GameDir -VanillaSide $VanillaSide -QuietConsole `
        -WindowSize $pass.Size -HarnessClient:$harness -ExtraArgs $extra | Out-Null

    # Wait for every modded instance to log "AutoTest: done".
    $deadline = $started.AddSeconds($timeout)
    function Done($log) { (Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started -and (Select-String -Path $log -Pattern 'AutoTest: done' -Quiet -Encoding utf8) }
    while ((Get-Date) -lt $deadline -and ($logs | Where-Object { -not (Done $_) })) { Start-Sleep -Milliseconds 250 }

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
        if (-not (Done $logs[$i])) { $failed++; Write-Host "[$($names[$i])] did not finish within ${timeout}s" -ForegroundColor Red }
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
        for ($t = 0; $t -lt 40 -and (Get-Process SlayTheSpire2 -ErrorAction SilentlyContinue); $t++) { Start-Sleep -Milliseconds 250 }
    }
}

if ($Suite -ne 'mixed') { & (Join-Path $PSScriptRoot 'test-settings.ps1') -Mode restore -GameDir $GameDir | Out-Null }
$elapsed = ((Get-Date) - $runStarted).TotalSeconds
$color = if ($failed -eq 0) { 'Green' } else { 'Red' }
$what = if ($Suite -eq 'full' -and $SkipFast) { 'full suite (without the fast pass)' } else { "$Suite suite" }
Write-Host ("{0}: {1} in {2:0}s" -f $what, $(if ($failed -eq 0) { 'ALL PASSED' } else { "$failed FAILED" }), $elapsed) -ForegroundColor $color
exit $(if ($failed -eq 0) { 0 } else { 1 })
