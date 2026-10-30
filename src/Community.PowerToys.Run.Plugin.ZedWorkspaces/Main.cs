// Copyright (c) Microsoft Corporation.
// Context-menu interactions adapted from PowerToys VS Code Workspaces.
// Licensed under the MIT license; see LICENSE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ManagedCommon;
using Wox.Infrastructure;
using Wox.Plugin;
using Wox.Plugin.Logger;

namespace Community.PowerToys.Run.Plugin.ZedWorkspaces;

public sealed class Main : IPlugin, IContextMenu
{
    private readonly ZedWorkspacesApi _workspacesApi = new(diagnostic: LogDiagnostic);
    private PluginInitContext? _context;

    public static string PluginID => "C47048261CB441C0AE406693557896EA";

    public string Name => "Zed Workspaces";

    public string Description => "Search and reopen recent local Zed Stable workspaces.";

    public void Init(PluginInitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    public List<Result> Query(Query query)
    {
        if (query is null)
        {
            return [];
        }

        var matches = ZedWorkspacesApi.Search(_workspacesApi.GetWorkspaces(), query.Search);
        var theme = _context?.API.GetCurrentTheme() ?? Theme.Light;
        var iconPath = theme is Theme.Light or Theme.HighContrastWhite
            ? "Images\\workspace.light.png"
            : "Images\\workspace.dark.png";
        var results = new List<Result>(matches.Count);
        var score = matches.Count;
        foreach (var workspace in matches)
        {
            results.Add(new Result
            {
                Title = workspace.Name,
                SubTitle = workspace.DisplayPath,
                IcoPath = iconPath,
                ToolTipData = new ToolTipData(workspace.Name, string.Join(Environment.NewLine, workspace.Roots)),
                ContextData = workspace,
                Score = score--,
                // The API already orders matches by relevance and last opened time.
                DisableUsageBasedScoring = true,
                Action = _ => Execute("Unable to open this workspace in Zed.", () =>
                    StartProcess(ZedInstance.CreateStartInfo(workspace))),
            });
        }

        return results;
    }

    public List<ContextMenuResult> LoadContextMenus(Result selectedResult)
    {
        if (selectedResult?.ContextData is not ZedWorkspace workspace)
        {
            return [];
        }

        var directory = workspace.PrimaryDirectory;
        return
        [
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Copy primary folder path (Ctrl+C)",
                Glyph = "\uE8C8",
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                AcceleratorKey = Key.C,
                AcceleratorModifiers = ModifierKeys.Control,
                Action = _ => Execute("Unable to copy the folder path to the clipboard.", () => Clipboard.SetText(directory)),
            },
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open primary folder in Explorer (Ctrl+Shift+F)",
                Glyph = "\uEC50",
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                AcceleratorKey = Key.F,
                AcceleratorModifiers = ModifierKeys.Control | ModifierKeys.Shift,
                Action = _ => Execute("Unable to open the primary folder in Explorer.", () =>
                {
                    RequireDirectory(directory);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                        UseShellExecute = false,
                    };
                    startInfo.ArgumentList.Add(directory);
                    StartProcess(startInfo);
                }),
            },
            new ContextMenuResult
            {
                PluginName = Name,
                Title = "Open primary folder in console (Ctrl+Shift+C)",
                Glyph = "\uE756",
                FontFamily = "Segoe Fluent Icons,Segoe MDL2 Assets",
                AcceleratorKey = Key.C,
                AcceleratorModifiers = ModifierKeys.Control | ModifierKeys.Shift,
                Action = _ => Execute("Unable to open the primary folder in the console.", () =>
                {
                    RequireDirectory(directory);
                    // PowerToys 0.102.3 passes this only as cmd.exe's working directory.
                    using var process = Helper.OpenInConsole(directory)
                        ?? throw new InvalidOperationException("PowerToys did not start the console process.");
                }),
            },
        ];
    }

    private static void StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows did not start the requested process.");
    }

    private static void RequireDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The folder does not exist or is unavailable: {directory}");
        }
    }

    private bool Execute(string message, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            Log.Exception(message, exception, typeof(Main));
            _context?.API.ShowMsg($"Plugin: {Name}", $"{message}{Environment.NewLine}{exception.Message}");
            return false;
        }
    }

    private static void LogDiagnostic(string message, Exception? exception)
    {
        if (exception is null)
        {
            Log.Warn(message, typeof(Main));
        }
        else
        {
            Log.Exception(message, exception, typeof(Main));
        }
    }
}
