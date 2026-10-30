using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using Microsoft.Data.Sqlite;

namespace Community.PowerToys.Run.Plugin.ZedWorkspaces;

public sealed class ZedWorkspacesApi
{
    // Zed c4273271092b0287f7d181f616bd5171e0a2f70c uses zero-based
    // domain migrations, not PRAGMA user_version, to version its workspace schema.
    private const long SupportedWorkspaceMigration = 41;

    private static readonly (string Name, string Type)[] WorkspaceColumns =
    [
        ("workspace_id", "INTEGER"),
        ("paths", "TEXT"),
        ("paths_order", "TEXT"),
        ("remote_connection_id", "INTEGER"),
        ("timestamp", "TEXT"),
        ("identity_paths", "TEXT"),
        ("identity_paths_order", "TEXT"),
    ];

    private static readonly (string Name, string Type)[] MigrationColumns =
    [
        ("domain", "TEXT"),
        ("step", "INTEGER"),
        ("migration", "TEXT"),
    ];

    private readonly string _databasePath;
    private readonly Action<string, Exception?>? _diagnostic;
    private readonly TimeSpan _refreshInterval;
    private readonly object _cacheLock = new();
    private IReadOnlyList<ZedWorkspace> _cachedWorkspaces = Array.Empty<ZedWorkspace>();
    private long _lastRefresh;
    private bool _hasRefreshed;

    public ZedWorkspacesApi(
        string? databasePath = null,
        Action<string, Exception?>? diagnostic = null,
        TimeSpan? refreshInterval = null)
    {
        _databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zed", "db", "0-stable", "db.sqlite");
        _diagnostic = diagnostic;
        _refreshInterval = refreshInterval ?? TimeSpan.FromSeconds(2);
        if (_refreshInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshInterval), "The refresh interval cannot be negative.");
        }
    }

    public IReadOnlyList<ZedWorkspace> GetWorkspaces()
    {
        lock (_cacheLock)
        {
            var now = Stopwatch.GetTimestamp();
            if (_hasRefreshed && Stopwatch.GetElapsedTime(_lastRefresh, now) < _refreshInterval)
            {
                return _cachedWorkspaces;
            }

            _hasRefreshed = true;
            _lastRefresh = now;
            // Failed or incompatible refreshes replace the cache: old entries must
            // not imply that an unsupported database can still be read safely.
            _cachedWorkspaces = ReadWorkspaces();
            return _cachedWorkspaces;
        }
    }

    public static IReadOnlyList<ZedWorkspace> Search(IReadOnlyList<ZedWorkspace> workspaces, string query)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(query);
        query = query.Trim();

        if (query.Length == 0)
        {
            for (var i = 1; i < workspaces.Count; i++)
            {
                if (CompareRecency(workspaces[i - 1], workspaces[i]) > 0)
                {
                    var sorted = workspaces.ToArray();
                    Array.Sort(sorted, CompareRecency);
                    return sorted;
                }
            }

            return workspaces;
        }

        var matches = new List<(ZedWorkspace Workspace, int Rank)>();
        foreach (var workspace in workspaces)
        {
            var rank = GetMatchRank(workspace, query);
            if (rank >= 0)
            {
                matches.Add((workspace, rank));
            }
        }

        if (matches.Count == 0)
        {
            return Array.Empty<ZedWorkspace>();
        }

        matches.Sort(static (left, right) =>
        {
            var rank = left.Rank.CompareTo(right.Rank);
            return rank != 0 ? rank : CompareRecency(left.Workspace, right.Workspace);
        });
        var results = new ZedWorkspace[matches.Count];
        for (var i = 0; i < matches.Count; i++)
        {
            results[i] = matches[i].Workspace;
        }

        return results;
    }

    private IReadOnlyList<ZedWorkspace> ReadWorkspaces()
    {
        if (!File.Exists(_databasePath))
        {
            Report($"Zed Stable workspace database was not found: {_databasePath}");
            return Array.Empty<ZedWorkspace>();
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            // Do not use immutable=1: it can hide Zed's live WAL. A deferred
            // transaction reads metadata and rows from one snapshot without
            // acquiring a writer lock or changing journal/checkpoint settings.
            using var transaction = connection.BeginTransaction(deferred: true);
            if (!IsCompatible(connection, transaction))
            {
                return Array.Empty<ZedWorkspace>();
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT workspace_id, paths, paths_order, remote_connection_id, timestamp
                FROM main.workspaces;
                """;
            using var reader = command.ExecuteReader();
            var unique = new Dictionary<string, ZedWorkspace>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var workspace = ReadWorkspace(reader);
                if (workspace is null)
                {
                    continue;
                }

                if (!unique.TryGetValue(workspace.Identity, out var previous)
                    || workspace.LastOpened > previous.LastOpened
                    || (workspace.LastOpened == previous.LastOpened && workspace.Id > previous.Id))
                {
                    unique[workspace.Identity] = workspace;
                }
            }

            var workspaces = unique.Values.ToArray();
            Array.Sort(workspaces, CompareRecency);
            return Array.AsReadOnly(workspaces);
        }
        catch (Exception exception) when (exception is SqliteException or IOException
            or UnauthorizedAccessException or SecurityException or ArgumentException
            or InvalidOperationException or NotSupportedException)
        {
            Report($"Unable to read Zed Stable workspace database: {_databasePath}", exception);
            return Array.Empty<ZedWorkspace>();
        }
    }

    private bool IsCompatible(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA main.user_version;";
        if (command.ExecuteScalar() is not long userVersion || userVersion != 0)
        {
            Report("Unsupported Zed workspace database user_version; expected 0.");
            return false;
        }

        if (!HasColumns(connection, transaction, "migrations", MigrationColumns))
        {
            return false;
        }

        command.CommandText = "SELECT MAX(step) FROM main.migrations WHERE domain = $domain;";
        command.Parameters.AddWithValue("$domain", "WorkspaceDb");
        if (command.ExecuteScalar() is not long migration)
        {
            Report("Zed workspace database has no valid WorkspaceDb migration history.");
            return false;
        }

        if (migration != SupportedWorkspaceMigration)
        {
            var age = migration > SupportedWorkspaceMigration ? "newer" : "older";
            Report($"Zed WorkspaceDb migration {migration} is {age} than supported migration {SupportedWorkspaceMigration}; the database will not be read.");
            return false;
        }

        return HasColumns(connection, transaction, "workspaces", WorkspaceColumns);
    }

    private bool HasColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        (string Name, string Type)[] expected)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table == "workspaces"
            ? "PRAGMA main.table_info('workspaces');"
            : "PRAGMA main.table_info('migrations');";
        using var reader = command.ExecuteReader();
        var columns = new Dictionary<string, (string Type, long PrimaryKey)>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            columns[reader.GetString(1)] = (reader.GetString(2).Trim(), reader.GetInt64(5));
        }

        foreach (var column in expected)
        {
            if (!columns.TryGetValue(column.Name, out var actual)
                || !actual.Type.Equals(column.Type, StringComparison.OrdinalIgnoreCase))
            {
                Report($"Incompatible Zed workspace schema: {table}.{column.Name} must be declared {column.Type}.");
                return false;
            }
        }

        if (table == "workspaces" && columns["workspace_id"].PrimaryKey != 1)
        {
            Report("Incompatible Zed workspace schema: workspace_id must be the primary key.");
            return false;
        }

        return true;
    }

    private ZedWorkspace? ReadWorkspace(SqliteDataReader reader)
    {
        if (reader.GetFieldType(0) != typeof(long))
        {
            Report("Skipping a Zed workspace with a non-integer workspace_id.");
            return null;
        }

        var id = reader.GetInt64(0);
        if (!reader.IsDBNull(3))
        {
            if (reader.GetFieldType(3) != typeof(long))
            {
                ReportSkipped(id, "remote_connection_id is not an integer");
            }

            return null;
        }

        if (reader.IsDBNull(1))
        {
            return null;
        }

        if (reader.GetFieldType(1) != typeof(string)
            || (!reader.IsDBNull(2) && reader.GetFieldType(2) != typeof(string))
            || reader.GetFieldType(4) != typeof(string))
        {
            ReportSkipped(id, "paths, paths_order, or timestamp has an unexpected SQLite storage type");
            return null;
        }

        var serializedPaths = reader.GetString(1);
        if (serializedPaths.Length == 0)
        {
            return null;
        }

        if (!DateTimeOffset.TryParseExact(
            reader.GetString(4),
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var lastOpened))
        {
            ReportSkipped(id, "timestamp is not a valid UTC yyyy-MM-dd HH:mm:ss value");
            return null;
        }

        var serializedOrder = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (!TryReadRoots(serializedPaths, serializedOrder, out var roots, out var problem))
        {
            ReportSkipped(id, problem);
            return null;
        }

        // identity_paths is a Git-worktree hint, not the complete reopen root
        // collection. Using it could collapse distinct local workspace groups.
        return new ZedWorkspace(id, Array.AsReadOnly(roots), lastOpened);
    }

    private static bool TryReadRoots(
        string serializedPaths,
        string? serializedOrder,
        out string[] roots,
        out string problem)
    {
        roots = serializedPaths.Split('\n');
        problem = string.Empty;
        var hasDirectory = false;
        for (var i = 0; i < roots.Length; i++)
        {
            if (!TryNormalizeRoot(roots[i], out var normalized, out problem))
            {
                return false;
            }

            roots[i] = normalized;
            if (Directory.Exists(normalized))
            {
                hasDirectory = true;
            }
            else if (!File.Exists(normalized))
            {
                problem = $"a root does not exist or is inaccessible: {normalized}";
                return false;
            }
        }

        if (!hasDirectory)
        {
            problem = "the workspace has no directory root";
            return false;
        }

        if (string.IsNullOrEmpty(serializedOrder))
        {
            return true;
        }

        var orderedRoots = new string[roots.Length];
        var seen = new bool[roots.Length];
        var serializedIndex = 0;
        foreach (var range in serializedOrder.AsSpan().Split(','))
        {
            if (serializedIndex >= roots.Length
                || !int.TryParse(serializedOrder.AsSpan()[range], NumberStyles.None, CultureInfo.InvariantCulture, out var originalIndex)
                || originalIndex < 0 || originalIndex >= roots.Length || seen[originalIndex])
            {
                problem = "paths_order is not a complete permutation of the root indices";
                return false;
            }

            // Zed stores (sorted path, original index), so reopening requires
            // the inverse permutation, not paths[paths_order[i]].
            orderedRoots[originalIndex] = roots[serializedIndex++];
            seen[originalIndex] = true;
        }

        if (serializedIndex != roots.Length)
        {
            problem = "paths_order does not contain one index for every root";
            return false;
        }

        roots = orderedRoots;
        return true;
    }

    private static bool TryNormalizeRoot(string root, out string normalized, out string problem)
    {
        normalized = string.Empty;
        problem = string.Empty;
        var path = root.Replace('/', '\\');
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            problem = "device paths are not supported workspace roots";
            return false;
        }

        var isDrivePath = path.Length >= 3
            && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var isUncPath = path.StartsWith(@"\\", StringComparison.Ordinal);
        if (isUncPath)
        {
            var serverEnd = path.IndexOf('\\', 2);
            if (serverEnd <= 2 || serverEnd == path.Length - 1 || path[serverEnd + 1] == '\\')
            {
                problem = "a UNC root must include a server and share";
                return false;
            }

            var server = path.AsSpan(2, serverEnd - 2).TrimEnd('.');
            if (server.Equals("wsl$", StringComparison.OrdinalIgnoreCase)
                || server.Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase))
            {
                problem = "WSL UNC workspace roots are not supported";
                return false;
            }
        }

        if ((!isDrivePath && !isUncPath) || !Path.IsPathFullyQualified(path))
        {
            problem = "a workspace root is not an absolute Windows drive or UNC path";
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length == 2 && normalized[1] == ':')
            {
                normalized += Path.DirectorySeparatorChar;
            }

            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"a workspace root is not a valid Windows path: {exception.Message}";
            return false;
        }
    }

    private static int GetMatchRank(ZedWorkspace workspace, string query)
    {
        if (workspace.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (workspace.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (workspace.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return workspace.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ? 3 : -1;
    }

    private static int CompareRecency(ZedWorkspace left, ZedWorkspace right)
    {
        var recency = right.LastOpened.CompareTo(left.LastOpened);
        if (recency != 0)
        {
            return recency;
        }

        var name = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
        return name != 0 ? name : right.Id.CompareTo(left.Id);
    }

    private void ReportSkipped(long id, string problem) => Report($"Skipping Zed workspace {id}: {problem}.");

    private void Report(string message, Exception? exception = null) => _diagnostic?.Invoke(message, exception);
}
