# Build a Release DLL and stage it in workshop/content for MegaCrit's ModUploader
# (https://github.com/megacrit/sts2-mod-uploader):
#   ModUploader.exe upload -w <repo>\workshop
# Steam must be running. The first upload writes workshop/mod_id.txt — commit it so later uploads update the
# same Workshop item.
param(
    [string]$Dotnet = (Join-Path $env:LOCALAPPDATA 'dotnet9\dotnet.exe')
)

$repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = Split-Path $Dotnet -Parent
& $Dotnet build (Join-Path $repo 'src\Spire2PileViewer.csproj') -c Release -nologo -v:q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$out = Join-Path $repo 'src\bin\Release\net9.0'
$content = Join-Path $repo 'workshop\content'
if (Test-Path $content) { Remove-Item -Recurse -Force $content }
New-Item -ItemType Directory -Path $content | Out-Null
Copy-Item (Join-Path $out 'Spire2PileViewer.dll'), (Join-Path $out 'Spire2PileViewer.json') $content
Get-ChildItem $content | Select-Object Name, Length
