[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$destination = Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\PowerToys Run\Plugins\ZedWorkspaces'
if (Get-Process PowerToys.PowerLauncher -ErrorAction SilentlyContinue) { throw 'Exit PowerToys before uninstalling.' }
if (Test-Path $destination) {
    $manifest = Join-Path $destination 'plugin.json'
    if (!(Test-Path $manifest) -or (Get-Content $manifest -Raw | ConvertFrom-Json).ID -ne 'C47048261CB441C0AE406693557896EA') { throw 'Destination does not belong to this plugin.' }
    Remove-Item $destination -Recurse -Force
}
Write-Output 'Zed Workspaces removed. Restart PowerToys. Zed user data was not touched.'
