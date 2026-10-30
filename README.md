# PowerToys Run Zed Workspaces

A PowerToys Run plugin that opens recent local Zed Stable projects on Windows x64. It reads Zed's SQLite history read-only and keeps no background process.

[中文说明](README_CN.md)

## Requirements

- PowerToys **0.102.3** (the .NET 10 Wox API)
- Windows x64
- Zed Stable
- .NET 10 SDK, to build

Other PowerToys or Zed versions are not verified.

## Build and install

```powershell
./scripts/Build.ps1
dotnet test ./tests/ZedWorkspaces.Tests/ZedWorkspaces.Tests.csproj -c Release
```

Artifacts: `artifacts/ZedWorkspaces/` and `artifacts/ZedWorkspaces-1.0.0-win-x64.zip`.

Exit PowerToys from the tray, then:

```powershell
./scripts/Install.ps1
./scripts/Uninstall.ps1   # to remove; exit PowerToys first
```

Restart PowerToys afterwards. The plugin is installed to `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\ZedWorkspaces`.

## Usage

- **Alt+Space**, then `z `: recent workspaces, newest first.
- `z name`: case-insensitive name or path search. Exact name, name prefix, name substring, then path match; recency breaks ties.
- **Enter**: open the workspace in Zed.
- **Ctrl+C** / **Ctrl+Shift+F** / **Ctrl+Shift+C**: copy, open in Explorer, or open in Command Prompt.

Multi-root workspaces open all roots together. The three context actions use the first directory only; the title lists every root name and the subtitle every full path.

Results refresh on query at most once every two seconds, so no restart is needed after Zed updates its history.

## Limits

Local Zed Stable only. Preview/Dev, WSL, SSH, remote projects and custom user-data directories are not supported. The database layout is tied to Zed `WorkspaceDb` migration 41; an unknown schema yields no results and a diagnostic instead of guessed data. See [implementation notes](documents/implementation.md) for the storage contract and failure handling.

Plugin errors and diagnostics go to the normal PowerToys Run logs under `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Logs`.

## License

MIT. The context-menu pattern follows Microsoft's MIT-licensed VS Code Workspaces plugin. See `LICENSE`.
