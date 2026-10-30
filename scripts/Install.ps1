[CmdletBinding()]
param([string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\ZedWorkspaces'))
$ErrorActionPreference = 'Stop'
$destination = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\PowerToys Run\Plugins\ZedWorkspaces'
$manifest = Join-Path $Source 'plugin.json'
if (!(Test-Path $manifest)) { throw 'Build first: ./scripts/Build.ps1' }
$metadata = Get-Content $manifest -Raw | ConvertFrom-Json
if ($metadata.Name -ne 'Zed Workspaces' -or !(Test-Path (Join-Path $Source $metadata.ExecuteFileName))) { throw 'Invalid plugin package.' }
if (Get-Process PowerToys.PowerLauncher -ErrorAction SilentlyContinue) { throw 'Exit PowerToys before installing; then restart it after installation.' }
if (Test-Path $destination) {
    $existing = Join-Path $destination 'plugin.json'
    if (!(Test-Path $existing) -or (Get-Content $existing -Raw | ConvertFrom-Json).ID -ne $metadata.ID) { throw 'Destination does not belong to this plugin.' }
    Remove-Item $destination -Recurse -Force
}
New-Item $destination -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $Source '*') $destination -Recurse -Force
Get-ChildItem $destination -Recurse -File | Unblock-File
Write-Output "Installed: $destination. Restart PowerToys, then Alt+Space and z followed by a space."
