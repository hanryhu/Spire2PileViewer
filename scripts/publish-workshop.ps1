# Publish to the Steam Workshop and verify the live page. Steam must be running.
#   1. Builds Release into workshop/content (prepare-workshop.ps1).
#   2. Stages a temporary upload workspace (workshop.json, image.png, mod_id.txt, content/, previews/).
#   3. Uploads with MegaCrit's ModUploader (%LOCALAPPDATA%\sts2-mod-uploader).
#   4. Checks the live page: thumbnail and every preview must match the local files (by SHA1).
#
# Previews: the uploader calls Steam's "update preview" for every file whose name already exists on the item,
# and Steam drops a preview that is re-sent unchanged (v0.3.1 lost all six that way). So:
#   - previews unchanged on the live page -> no previews/ folder is uploaded (the uploader then leaves them alone);
#   - previews changed -> all are uploaded under version-stamped names (all new; the old ones are removed).
param(
    [string]$ChangeNote = '',   # overrides workshop.json's changeNote for this upload only
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$workshop = Join-Path $repo 'workshop'
$itemId = (Get-Content (Join-Path $workshop 'mod_id.txt') -Raw).Trim()
$version = (Get-Content (Join-Path $repo 'src\Spire2PileViewer.json') -Raw | ConvertFrom-Json).version
$pageUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=$itemId"

function Sha1($path) { (Get-FileHash -Algorithm SHA1 $path).Hash.ToUpperInvariant() }

function SameSet($a, $b) { (@($a | Sort-Object) -join ',') -eq (@($b | Sort-Object) -join ',') }

function LiveImages {
    # Every image on the public page (thumbnail + previews); their URLs end in the file's SHA1.
    $html = (Invoke-WebRequest -UseBasicParsing "$pageUrl&nocache=$(Get-Random)").Content
    @([regex]::Matches($html, 'images\.steamusercontent\.com/ugc/\d+/([0-9A-F]{40})/') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
}

if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'prepare-workshop.ps1') | Out-Null }
$content = Join-Path $workshop 'content'
if (-not (Test-Path (Join-Path $content 'Spire2PileViewer.dll'))) { throw 'No build in workshop/content.' }

$previewFiles = @(Get-ChildItem (Join-Path $workshop 'previews') -Filter *.png | Sort-Object Name)
$localPreviews = @($previewFiles | ForEach-Object { Sha1 $_.FullName } | Sort-Object)
$thumbnail = Sha1 (Join-Path $workshop 'image.png')
$livePreviews = @(LiveImages | Where-Object { $_ -ne $thumbnail })
$previewsChanged = -not (SameSet $localPreviews $livePreviews)

# Stage the upload workspace.
$stage = Join-Path $env:TEMP "spire2pileviewer-workshop-$version"
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item (Join-Path $workshop 'image.png'), (Join-Path $workshop 'mod_id.txt') $stage
Copy-Item $content (Join-Path $stage 'content') -Recurse
$meta = Get-Content (Join-Path $workshop 'workshop.json') -Raw | ConvertFrom-Json
if ($ChangeNote) { $meta.changeNote = $ChangeNote }
[System.IO.File]::WriteAllText((Join-Path $stage 'workshop.json'), ($meta | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
if ($previewsChanged) {
    $stamp = "$version-$(Get-Date -Format 'yyyyMMddHHmm')"
    New-Item -ItemType Directory -Path (Join-Path $stage 'previews') | Out-Null
    foreach ($f in $previewFiles) { Copy-Item $f.FullName (Join-Path $stage "previews\$stamp-$($f.Name)") }
    Write-Host "Previews changed: uploading $($previewFiles.Count) under new names ($stamp-*)."
}
else {
    Write-Host 'Previews unchanged on the live page: leaving them alone.'
}

# Upload (the uploader must run from its own folder).
Push-Location (Join-Path $env:LOCALAPPDATA 'sts2-mod-uploader')
$ErrorActionPreference = 'Continue' # the uploader writes harmless Steam messages to stderr
try { & .\ModUploader.exe upload -w $stage 2>&1 | ForEach-Object { "$_" } | Where-Object { $_ -notmatch 'k_EItemUpdateStatus|NativeCommandError|^At |^\+|CategoryInfo|FullyQualifiedErrorId' } }
finally { Pop-Location; $ErrorActionPreference = 'Stop' }
if ($LASTEXITCODE -ne 0) { Write-Host "Upload failed (exit code $LASTEXITCODE)." -ForegroundColor Red; exit 1 }
Remove-Item -Recurse -Force $stage

# Verify the live page (it can take a little while to update).
$ok = $false
for ($i = 0; $i -lt 12 -and -not $ok; $i++) {
    Start-Sleep -Seconds 5
    $after = LiveImages
    $previewsNow = @($after | Where-Object { $_ -ne $thumbnail })
    $ok = ($after -contains $thumbnail) -and (SameSet $localPreviews $previewsNow)
}
$details = Invoke-RestMethod -Method Post 'https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/' `
    -Body @{ itemcount = 1; 'publishedfileids[0]' = $itemId }
$d = $details.response.publishedfiledetails[0]
Write-Host ("Live: visibility={0} (0 = public), file size={1}, updated {2}" -f $d.visibility, $d.file_size,
    [DateTimeOffset]::FromUnixTimeSeconds($d.time_updated).LocalDateTime)
if ($ok) {
    Write-Host "PASS: thumbnail and all $($localPreviews.Count) previews on the page match the local files." -ForegroundColor Green
    exit 0
}
Write-Host "FAIL: the page does not match (thumbnail on page: $($after -contains $thumbnail))." -ForegroundColor Red
$localPreviews | Where-Object { $previewsNow -notcontains $_ } | ForEach-Object { Write-Host "    missing on the page: $_" -ForegroundColor Red }
$previewsNow | Where-Object { $localPreviews -notcontains $_ } | ForEach-Object { Write-Host "    stale on the page:   $_" -ForegroundColor Red }
exit 1
