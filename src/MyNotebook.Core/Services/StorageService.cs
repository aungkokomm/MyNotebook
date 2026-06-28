using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MyNotebook.Core.Services;

/// <summary>
/// Opens the SQLite database and applies embedded migrations. The migration SQL
/// is the same file under db/migrations, embedded as a resource (single source).
/// </summary>
public sealed class StorageService : IStorageService
{
    private const int TargetVersion = 4;
    private const int KeepBackups = 10;
    private readonly string _connectionString;
    private readonly IPathService _paths;

    public StorageService(IPathService paths)
    {
        _paths = paths;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>
    /// Keep the last <see cref="KeepBackups"/> timestamped snapshots of the database in a
    /// <c>Backups\</c> folder next to it (portable — stays inside the app's Data folder). A safety
    /// net against accidental loss. Best-effort: never throws, never blocks startup, and an empty
    /// database is NOT backed up when good snapshots already exist (so a reset can't evict them).
    /// </summary>
    public void BackupOnLaunch()
    {
        try
        {
            var dir = System.IO.Path.Combine(_paths.DataRoot, "Backups");
            System.IO.Directory.CreateDirectory(dir);
            bool hasBackups = System.IO.Directory.GetFiles(dir, "notebook_*.db").Length > 0;

            long liveNotes;
            using (var con = OpenConnection())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM Notes";
                liveNotes = Convert.ToInt64(cmd.ExecuteScalar());
            }
            if (liveNotes == 0 && hasBackups) return;   // don't let an empty DB overwrite good history

            var dest = System.IO.Path.Combine(dir, $"notebook_{DateTime.Now:yyyy-MM-dd_HHmm}.db");
            if (!System.IO.File.Exists(dest))
            {
                using var src = OpenConnection();
                using var dst = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = dest,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                }.ToString());
                dst.Open();
                src.BackupDatabase(dst);   // consistent snapshot, includes WAL contents
            }

            foreach (var old in System.IO.Directory.GetFiles(dir, "notebook_*.db")
                                   .OrderByDescending(System.IO.Path.GetFileName).Skip(KeepBackups))
                try { System.IO.File.Delete(old); } catch { }
        }
        catch { /* backups are best-effort */ }
    }

    public SqliteConnection OpenConnection()
    {
        var con = new SqliteConnection(_connectionString);
        con.Open();
        // Performance pragmas: WAL (persistent), relaxed sync, in-memory temp, memory-map,
        // and a 16 MB page cache. One batched round-trip; cheap and pooling-friendly.
        using var pragma = con.CreateCommand();
        pragma.CommandText =
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            "PRAGMA temp_store=MEMORY;" +
            "PRAGMA cache_size=-16000;" +
            "PRAGMA mmap_size=67108864;";
        pragma.ExecuteNonQuery();
        return con;
    }

    public int SchemaVersion
    {
        get
        {
            using var con = OpenConnection();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Initialize()
    {
        using var con = OpenConnection();
        int current;
        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version";
            current = Convert.ToInt32(cmd.ExecuteScalar());
        }

        if (current >= TargetVersion)
            return;

        using var tx = con.BeginTransaction();
        if (current < 1) Run(con, tx, "001_initial_schema.sql");
        if (current < 2) Run(con, tx, "002_note_versions.sql");
        if (current < 3) Run(con, tx, "003_search_trigram.sql");
        if (current < 4) Run(con, tx, "004_timeline_index.sql");
        tx.Commit();
    }

    // Each migration file sets its own PRAGMA user_version at the end.
    private static void Run(SqliteConnection con, SqliteTransaction tx, string fileName)
    {
        using var cmd = con.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = LoadMigration(fileName);
        cmd.ExecuteNonQuery();
    }

    private static string LoadMigration(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Embedded as Migrations\<file>; resource name ends with ".Migrations.<file>".
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"Migrations.{fileName}", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded migration '{fileName}' not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
