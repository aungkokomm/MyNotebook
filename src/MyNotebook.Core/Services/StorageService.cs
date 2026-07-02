using System.Reflection;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
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

    private const string PendingZipName = "_pending_restore.zip";
    private const string PendingDbName = "_pending_restore.db";

    /// <inheritdoc/>
    public void CreateBackupZip(string destZipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "mnb_backup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            // Consistent single-file DB snapshot (checkpoints WAL contents in, so no -wal/-shm needed).
            var dbOut = Path.Combine(staging, "notebook.db");
            using (var src = OpenConnection())
            using (var dst = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = dbOut, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString()))
            {
                dst.Open();
                src.BackupDatabase(dst);
            }   // Pooling=false so the handle to the staging file is released before we zip it.

            var att = Path.Combine(_paths.DataRoot, "attachments");
            if (Directory.Exists(att)) CopyDir(att, Path.Combine(staging, "attachments"));

            var settings = Path.Combine(_paths.DataRoot, "settings.json");
            if (File.Exists(settings)) File.Copy(settings, Path.Combine(staging, "settings.json"), true);

            long notes = 0;
            try
            {
                using var con = OpenConnection();
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM Notes WHERE deleted=0";
                notes = Convert.ToInt64(cmd.ExecuteScalar());
            }
            catch { /* manifest count is best-effort */ }

            File.WriteAllText(Path.Combine(staging, "backup-manifest.json"), JsonSerializer.Serialize(new
            {
                app = "MyNotebook",
                kind = "full-backup",
                createdUtc = DateTime.UtcNow.ToString("o"),
                schema = SchemaVersion,
                notes,
            }));

            var dir = Path.GetDirectoryName(destZipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(destZipPath)) File.Delete(destZipPath);
            ZipFile.CreateFromDirectory(staging, destZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    /// <inheritdoc/>
    public (bool ok, string message) InspectBackup(string path)
    {
        try
        {
            if (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                var n = CountNotesInDbFile(path);
                return n < 0
                    ? (false, "This .db file could not be read as a My Notebook database.")
                    : (true, $"Database snapshot with {n} note{(n == 1 ? "" : "s")} (images are kept from your current notebook).");
            }

            using var zip = ZipFile.OpenRead(path);
            var hasDb = zip.GetEntry("notebook.db") != null;
            if (!hasDb) return (false, "This zip does not look like a My Notebook backup — it has no notebook.db inside.");

            var man = zip.GetEntry("backup-manifest.json");
            if (man != null)
            {
                using var s = man.Open();
                using var doc = JsonDocument.Parse(s);
                var root = doc.RootElement;
                long notes = root.TryGetProperty("notes", out var ne) ? ne.GetInt64() : -1;
                string? created = root.TryGetProperty("createdUtc", out var ce) ? ce.GetString() : null;
                var when = DateTime.TryParse(created, out var dt) ? dt.ToLocalTime().ToString("f") : "an earlier date";
                var count = notes >= 0 ? $"{notes} note{(notes == 1 ? "" : "s")}" : "your notes and images";
                return (true, $"My Notebook backup from {when}, containing {count}.");
            }
            return (true, "My Notebook backup (full folder archive).");
        }
        catch (Exception ex) { return (false, "Could not read this backup file: " + ex.Message); }
    }

    /// <inheritdoc/>
    public void StageRestore(string sourcePath)
    {
        var pz = Path.Combine(_paths.DataRoot, PendingZipName);
        var pd = Path.Combine(_paths.DataRoot, PendingDbName);
        try { if (File.Exists(pz)) File.Delete(pz); } catch { }
        try { if (File.Exists(pd)) File.Delete(pd); } catch { }
        var dest = sourcePath.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? pd : pz;
        File.Copy(sourcePath, dest, true);
    }

    /// <inheritdoc/>
    public void ApplyPendingRestoreIfAny()
    {
        var pz = Path.Combine(_paths.DataRoot, PendingZipName);
        var pd = Path.Combine(_paths.DataRoot, PendingDbName);
        bool haveZip = File.Exists(pz);
        bool haveDb = File.Exists(pd);
        if (!haveZip && !haveDb) return;

        try
        {
            if (haveZip)
            {
                var staging = Path.Combine(Path.GetTempPath(), "mnb_restore_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                try
                {
                    ZipFile.ExtractToDirectory(pz, staging, overwriteFiles: true);
                    var newDb = Path.Combine(staging, "notebook.db");
                    if (!File.Exists(newDb)) return;   // invalid archive — leave everything untouched

                    ReplaceDbFrom(newDb);

                    var newAtt = Path.Combine(staging, "attachments");
                    if (Directory.Exists(newAtt))
                    {
                        var liveAtt = Path.Combine(_paths.DataRoot, "attachments");
                        try { if (Directory.Exists(liveAtt)) Directory.Delete(liveAtt, true); } catch { }
                        CopyDir(newAtt, liveAtt);
                    }

                    var newSet = Path.Combine(staging, "settings.json");
                    if (File.Exists(newSet))
                        try { File.Copy(newSet, Path.Combine(_paths.DataRoot, "settings.json"), true); } catch { }
                }
                finally { try { Directory.Delete(staging, true); } catch { } }
            }
            else
            {
                ReplaceDbFrom(pd);   // db-only restore keeps the current attachments
            }
        }
        finally
        {
            // Only clear the pending markers after a completed attempt.
            try { if (File.Exists(pz)) File.Delete(pz); } catch { }
            try { if (File.Exists(pd)) File.Delete(pd); } catch { }
        }
    }

    /// <inheritdoc/>
    public void ReleaseConnections() => SqliteConnection.ClearAllPools();

    // Swap the live DB file for a restored one, dropping any stale WAL/SHM sidecars.
    private void ReplaceDbFrom(string newDb)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm", "" })
            TryFile(() => { var p = _paths.DbPath + suffix; if (File.Exists(p)) File.Delete(p); });
        TryFile(() => File.Copy(newDb, _paths.DbPath, true));
    }

    private long CountNotesInDbFile(string dbFile)
    {
        try
        {
            using var con = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Notes WHERE deleted=0";
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
        catch { return -1; }
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
    }

    // Retry a file op a few times — the just-closed process may still be releasing a handle.
    private static void TryFile(Action op)
    {
        for (int i = 0; i < 12; i++)
        {
            try { op(); return; }
            catch { System.Threading.Thread.Sleep(120); }
        }
        op();   // final attempt: let it throw so the caller knows it failed
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
