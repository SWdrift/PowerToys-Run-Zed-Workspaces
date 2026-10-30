[CmdletBinding()]
param([string]$PowerToysDirectory = "$env:ProgramFiles\PowerToys")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Community.PowerToys.Run.Plugin.ZedWorkspaces\Community.PowerToys.Run.Plugin.ZedWorkspaces.csproj'
$output = Join-Path $root 'artifacts\ZedWorkspaces'
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
& dotnet publish $project -c Release -o $output "-p:PowerToysDirectory=$PowerToysDirectory"
if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'README_CN.md') $output
$zip = Join-Path $root 'artifacts\ZedWorkspaces-1.0.0-win-x64.zip'
Compress-Archive -Path $output -DestinationPath $zip -Force
Write-Output "Plugin: $output"
Write-Output "Archive: $zip"
