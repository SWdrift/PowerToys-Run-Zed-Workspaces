using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace Community.PowerToys.Run.Plugin.ZedWorkspaces;

public static class ZedInstance
{
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths\zed.exe";

    public static ProcessStartInfo CreateStartInfo(ZedWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var executable = FindExecutable()
            ?? throw new FileNotFoundException("Zed Stable could not be found. Install Zed Stable for this user or this machine, or add its installation's bin directory to PATH, then restart PowerToys.");
        return CreateStartInfo(workspace, executable);
    }

    public static ProcessStartInfo CreateStartInfo(ZedWorkspace workspace, string executablePath)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The Zed executable must have a fully qualified path.", nameof(executablePath));
        }

        if (workspace.Roots.Count == 0)
        {
            throw new ArgumentException("The workspace has no local root folders.", nameof(workspace));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var root in workspace.Roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            {
                throw new ArgumentException("Every workspace root must have a fully qualified local path.", nameof(workspace));
            }

            // Zed accepts multiple positional paths. ArgumentList preserves them literally.
            startInfo.ArgumentList.Add(root);
        }

        return startInfo;
    }

    public static string? FindExecutable()
    {
        var executable = FindStableExecutable(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Zed", "Zed.exe"))
            ?? FindStableExecutable(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zed", "Zed.exe"))
            ?? FindStableExecutable(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Zed", "Zed.exe"))
            ?? FindAppPath(RegistryHive.CurrentUser, RegistryView.Registry64)
            ?? FindAppPath(RegistryHive.CurrentUser, RegistryView.Registry32)
            ?? FindAppPath(RegistryHive.LocalMachine, RegistryView.Registry64)
            ?? FindAppPath(RegistryHive.LocalMachine, RegistryView.Registry32);
        if (executable is not null)
        {
            return executable;
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(searchPath))
        {
            return null;
        }

        foreach (var directory in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            executable = FindStableExecutable(Path.Combine(directory.Trim('"'), "zed.exe"));
            if (executable is not null)
            {
                return executable;
            }
        }

        return null;
    }

    private static string? FindAppPath(RegistryHive hive, RegistryView view)
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var appPath = registry.OpenSubKey(AppPathsKey);
            return FindStableExecutable(appPath?.GetValue(null) as string);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    private static string? FindStableExecutable(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(candidate)
                || !string.Equals(Path.GetFileName(candidate), "zed.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(candidate))
            {
                return null;
            }

            // Official Windows resources distinguish "Zed" from "Zed Preview", "Zed Nightly", and "Zed Dev".
            // Check the executable itself, including when PATH resolves the bin/zed.exe CLI launcher.
            var version = FileVersionInfo.GetVersionInfo(candidate);
            return string.Equals(version.ProductName, "Zed", StringComparison.Ordinal)
                && string.Equals(version.CompanyName, "Zed Industries, Inc.", StringComparison.Ordinal)
                ? candidate
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or ArgumentException)
        {
            return null;
        }
    }
}
