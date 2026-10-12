# Install the locally built Spire2PileViewer (src/bin/Debug/net9.0) into the game's mods folder.
# Close the game first; the DLL is locked while it runs.
param(
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2',
    [string]$Configuration = 'Debug'
)

$src = Join-Path $PSScriptRoot "..\src\bin\$Configuration\net9.0"
$dest = Join-Path $GameDir 'mods\Spire2PileViewer'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item (Join-Path $src 'Spire2PileViewer.dll'), (Join-Path $src 'Spire2PileViewer.json') $dest -Force
Get-ChildItem $dest | Select-Object Name, Length, LastWriteTime
