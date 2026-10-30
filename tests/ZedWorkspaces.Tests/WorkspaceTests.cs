using System.Diagnostics;
using System.IO;
using Community.PowerToys.Run.Plugin.ZedWorkspaces;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ZedWorkspaces.Tests;

public sealed class WorkspaceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ZedWorkspacesTests-" + Guid.NewGuid());
    private string Database => Path.Combine(directory, "db.sqlite");
    public WorkspaceTests() => Directory.CreateDirectory(directory);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    private string Folder(string name) { var path = Path.Combine(directory, name); Directory.CreateDirectory(path); return path; }
    private SqliteConnection CreateDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE migrations(domain TEXT, step INTEGER, migration TEXT);
            INSERT INTO migrations VALUES('WorkspaceDb',41,'fixture');
            CREATE TABLE workspaces(workspace_id INTEGER PRIMARY KEY, paths TEXT, paths_order TEXT,
                remote_connection_id INTEGER, timestamp TEXT NOT NULL, identity_paths TEXT, identity_paths_order TEXT);
            """;
        command.ExecuteNonQuery();
        return connection;
    }
    private static void Insert(SqliteConnection connection, long id, string roots, string order, string time, long? remote = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO workspaces(workspace_id,paths,paths_order,timestamp,remote_connection_id) VALUES($id,$paths,$order,$time,$remote)";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$paths", roots);
        command.Parameters.AddWithValue("$order", order); command.Parameters.AddWithValue("$time", time);
        command.Parameters.AddWithValue("$remote", (object?)remote ?? DBNull.Value); command.ExecuteNonQuery();
    }
    [Fact]
    public void ReadsCommittedWalAndKeepsWholeDistinctRootSets()
    {
        using var writer = CreateDatabase();
        var a = Folder("a"); var b = Folder("b"); var other = Folder("other\\a");
        Insert(writer, 1, a, "0", "2026-10-01 01:00:00");
        Insert(writer, 2, a.ToUpperInvariant() + "\\", "0", "2026-10-02 01:00:00");
        Insert(writer, 3, a + "\n" + b, "1,0", "2026-10-03 01:00:00");
        Insert(writer, 4, other, "0", "2026-10-04 01:00:00");
        var api = new ZedWorkspacesApi(Database);
        var rows = api.GetWorkspaces();
        Assert.Equal(new long[] { 4, 3, 2 }, rows.Select(x => x.Id));
        Assert.Equal(new[] { b, a }, rows[1].Roots);
        Assert.Equal(b, rows[1].PrimaryDirectory);
        Assert.Equal(TimeSpan.Zero, rows[0].LastOpened.Offset);
        using var tx = writer.BeginTransaction();
        using var command = writer.CreateCommand(); command.Transaction = tx;
        command.CommandText = "UPDATE workspaces SET timestamp='2026-10-09 00:00:00' WHERE workspace_id=2"; command.ExecuteNonQuery();
        var independent = new ZedWorkspacesApi(Database).GetWorkspaces();
        Assert.Equal(4, independent[0].Id);
        tx.Rollback();
    }
    [Fact]
    public void RestoresInverseDisplayPermutationWithoutMergingOverlappingGroups()
    {
        using var writer = CreateDatabase();
        var a = Folder("a"); var b = Folder("b"); var c = Folder("c");
        Insert(writer, 1, a + "\n" + b + "\n" + c, "2,0,1", "2026-10-01 00:00:00");
        Insert(writer, 2, a + "\n" + b, "0,1", "2026-10-02 00:00:00");
        var rows = new ZedWorkspacesApi(Database).GetWorkspaces();
        Assert.Equal(new long[] { 2, 1 }, rows.Select(x => x.Id));
        Assert.Equal(new[] { b, c, a }, rows[1].Roots);
    }
    [Fact]
    public void RejectsInvalidRowsWithoutLosingValidWorkspace()
    {
        using var writer = CreateDatabase(); var a = Folder("中文 空格 & % #");
        Insert(writer, 1, a, "0", "2026-10-01 00:00:00");
        Insert(writer, 2, Path.Combine(directory, "missing"), "0", "2026-10-02 00:00:00");
        Insert(writer, 3, a, "0", "2026-10-03 00:00:00", 42);
        Insert(writer, 4, a + "\n" + Path.Combine(directory, "missing"), "0,1", "2026-10-04 00:00:00");
        Insert(writer, 5, a, "0", "not-a-timestamp");
        Insert(writer, 6, "\\\\wsl.localhost\\Ubuntu\\home\\me", "0", "2026-10-04 00:00:00");
        Insert(writer, 7, a, "9", "2026-10-04 00:00:00");
        Assert.Equal(1, Assert.Single(new ZedWorkspacesApi(Database).GetWorkspaces()).Id);
    }
    [Fact]
    public void MissingIncompatibleAndFutureDatabasesFailClosedWithDiagnostics()
    {
        var messages = new List<string>();
        var api = new ZedWorkspacesApi(Database, (text, _) => messages.Add(text), TimeSpan.Zero);
        Assert.Empty(api.GetWorkspaces()); Assert.NotEmpty(messages);
        using var writer = CreateDatabase(); var a = Folder("valid");
        Insert(writer, 1, a, "0", "2026-10-01 00:00:00");
        Assert.Single(api.GetWorkspaces());
        using var command = writer.CreateCommand(); command.CommandText = "UPDATE migrations SET step=42"; command.ExecuteNonQuery();
        Assert.Empty(api.GetWorkspaces());
        command.CommandText = "DROP TABLE workspaces"; command.ExecuteNonQuery();
        Assert.Empty(api.GetWorkspaces());
        Assert.True(messages.Count >= 3);
    }
    [Fact]
    public void FileRootsAreRetainedButFileOnlyWorkspaceIsNotAProject()
    {
        using var writer = CreateDatabase();
        var a = Folder("directory");
        var file = Path.Combine(directory, "notes 中文.txt"); File.WriteAllText(file, "fixture");
        Insert(writer, 1, file, "0", "2026-10-01 00:00:00");
        Insert(writer, 2, a + "\n" + file, "1,0", "2026-10-02 00:00:00");
        var row = Assert.Single(new ZedWorkspacesApi(Database).GetWorkspaces());
        Assert.Equal(2, row.Id);
        Assert.Equal(new[] { file, a }, row.Roots);
        Assert.Equal(a, row.PrimaryDirectory);
    }
    [Fact]
    public void IncompatibleColumnTypeProducesDiagnosticInsteadOfGuessedPaths()
    {
        using var writer = CreateDatabase();
        using var command = writer.CreateCommand();
        command.CommandText = "DROP TABLE workspaces; CREATE TABLE workspaces(workspace_id INTEGER PRIMARY KEY, paths BLOB, paths_order TEXT, remote_connection_id INTEGER, timestamp TEXT, identity_paths TEXT, identity_paths_order TEXT)";
        command.ExecuteNonQuery();
        var messages = new List<string>();
        Assert.Empty(new ZedWorkspacesApi(Database, (text, _) => messages.Add(text)).GetWorkspaces());
        Assert.NotEmpty(messages);
    }
    [Fact]
    public void SnapshotDoesNotRescanEachKeystrokeAndRefreshesOnDemand()
    {
        using var writer = CreateDatabase(); var a = Folder("first"); var b = Folder("second");
        Insert(writer, 1, a, "0", "2026-10-01 00:00:00");
        var api = new ZedWorkspacesApi(Database, refreshInterval: TimeSpan.FromHours(1));
        Assert.Equal(1, Assert.Single(api.GetWorkspaces()).Id);
        Insert(writer, 2, b, "0", "2026-10-02 00:00:00");
        Assert.Equal(1, Assert.Single(api.GetWorkspaces()).Id);
        Assert.Equal(new long[] { 2, 1 }, new ZedWorkspacesApi(Database).GetWorkspaces().Select(x => x.Id));
    }
    [Fact]
    public void SearchRanksNameBeforePathAndThenRecency()
    {
        var time = DateTimeOffset.UtcNow;
        var rows = new[] {
            new ZedWorkspace(1, new[] { "C:\\work\\Orbit" }, time.AddDays(-5)),
            new ZedWorkspace(2, new[] { "C:\\work\\orbit-tools" }, time.AddDays(-4)),
            new ZedWorkspace(3, new[] { "C:\\work\\my-orbit" }, time.AddDays(-3)),
            new ZedWorkspace(4, new[] { "C:\\orbit\\other" }, time),
            new ZedWorkspace(5, new[] { "C:\\other\\ORBIT" }, time.AddDays(-1)),
            new ZedWorkspace(6, new[] { "C:\\work\\中文项目" }, time.AddHours(-1))
        };
        Assert.Equal(new long[] { 5, 1, 2, 3, 4 }, ZedWorkspacesApi.Search(rows, "OrBiT").Select(x => x.Id));
        Assert.Equal(6, Assert.Single(ZedWorkspacesApi.Search(rows, "中文")).Id);
        Assert.Equal(4, Assert.Single(ZedWorkspacesApi.Search(rows, "orbit\\other")).Id);
        Assert.Empty(ZedWorkspacesApi.Search(rows, "absent"));
        Assert.Equal(4, ZedWorkspacesApi.Search(rows, "")[0].Id);
    }
    [Fact]
    public void LaunchArgumentsRoundTripUnicodeSpacesAndMetacharacters()
    {
        var roots = new[] { Folder("中文 空格 & % # (a)"), Folder("second ; ' root") };
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        var info = ZedInstance.CreateStartInfo(new ZedWorkspace(1, roots, DateTimeOffset.UtcNow), powershell);
        // Launch a real child process to check OS argument transport, not just a property echo.
        var original = info.ArgumentList.ToArray(); info.ArgumentList.Clear();
        var script = Path.Combine(directory, "arguments.ps1");
        File.WriteAllText(script, "$args | ConvertTo-Json -Compress");
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        foreach (var arg in original) info.ArgumentList.Add(arg);
        info.RedirectStandardOutput = true; info.RedirectStandardError = true; info.CreateNoWindow = true;
        using var child = Process.Start(info)!;
        var stdout = child.StandardOutput.ReadToEnd(); var stderr = child.StandardError.ReadToEnd(); child.WaitForExit();
        Assert.True(child.ExitCode == 0, stderr);
        Assert.Equal(roots, System.Text.Json.JsonSerializer.Deserialize<string[]>(stdout));
    }
}
