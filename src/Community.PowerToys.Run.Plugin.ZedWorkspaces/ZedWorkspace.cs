using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Community.PowerToys.Run.Plugin.ZedWorkspaces;

public sealed record ZedWorkspace(long Id, IReadOnlyList<string> Roots, DateTimeOffset LastOpened)
{
    public IReadOnlyList<string> Roots { get; } = Roots;

    public DateTimeOffset LastOpened { get; } = LastOpened.ToUniversalTime();

    public string Name { get; } = string.Join(", ", Roots.Select(GetRootName));

    public string PrimaryDirectory { get; } = Roots.FirstOrDefault(Directory.Exists) ?? string.Empty;

    public string DisplayPath { get; } = string.Join("; ", Roots);

    public string Identity { get; } = string.Join(
        "\n",
        Roots.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();

    private static string GetRootName(string root)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? root : name;
    }
}
