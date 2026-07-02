using System.IO;
using System.IO.Compression;
using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

/// <summary>
/// The data-safety net: a backup must be restorable. These exercise the full round-trip
/// (back up -> change everything -> restore -> the original notes and images are back),
/// simulating the real two-phase restore where a fresh process applies the staged file
/// before opening the DB.
/// </summary>
public class BackupRestoreTests
{
    private static string WriteAttachment(string root, string rel, byte[] bytes)
    {
        var abs = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllBytes(abs, bytes);
        return abs;
    }

    [Fact]
    public void Zip_backup_restores_notes_and_attachments_after_everything_changed()
    {
        using var fx = new NotebookFixture();
        var att = new byte[] { 1, 2, 3, 4, 5 };
        var picAbs = WriteAttachment(fx.Root, "attachments/screenshots/1/pic.png", att);

        var alpha = fx.Notes.CreateNote("alpha");
        alpha.BodyPlain = "unique_marker_alpha";
        fx.Notes.UpdateNote(alpha);
        fx.Notes.AddImage(alpha.Id, "attachments/screenshots/1/pic.png", 10, 10);

        var zip = Path.Combine(fx.Root, "backup_test.zip");
        fx.Storage.CreateBackupZip(zip);

        var (ok, _) = fx.Storage.InspectBackup(zip);
        Assert.True(ok);

        // Wreck everything the backup should be able to bring back.
        fx.Notes.SoftDeleteNote(alpha.Id);
        fx.Notes.DeleteNoteForever(alpha.Id);
        var beta = fx.Notes.CreateNote("beta");
        beta.BodyPlain = "post_backup_beta";
        fx.Notes.UpdateNote(beta);
        File.Delete(picAbs);
        Assert.False(File.Exists(picAbs));

        // Simulate a fresh process: new StorageService applies the staged restore BEFORE opening the DB.
        fx.Storage.StageRestore(zip);
        var storage2 = new StorageService(fx.Paths);
        storage2.ApplyPendingRestoreIfAny();
        storage2.Initialize();
        var notes2 = new NoteService(storage2);

        Assert.NotEmpty(notes2.Search("unique_marker_alpha"));   // original note is back
        Assert.Empty(notes2.Search("post_backup_beta"));         // post-backup note is gone
        Assert.True(File.Exists(picAbs));                        // attachment restored
        Assert.Equal(att, File.ReadAllBytes(picAbs));            // and byte-identical
    }

    [Fact]
    public void Db_snapshot_restore_brings_back_the_db_and_keeps_current_attachments()
    {
        using var fx = new NotebookFixture();

        var gamma = fx.Notes.CreateNote("gamma");
        gamma.BodyPlain = "unique_marker_gamma";
        fx.Notes.UpdateNote(gamma);

        // Produce a raw .db snapshot (as the Backups\ folder holds) by pulling notebook.db out of a zip.
        var zip = Path.Combine(fx.Root, "snap.zip");
        fx.Storage.CreateBackupZip(zip);
        var dbSnap = Path.Combine(fx.Root, "snap.db");
        using (var z = ZipFile.OpenRead(zip))
            z.GetEntry("notebook.db")!.ExtractToFile(dbSnap, true);

        var (ok, _) = fx.Storage.InspectBackup(dbSnap);
        Assert.True(ok);

        // An attachment created AFTER the snapshot must survive a db-only restore.
        var keepAbs = WriteAttachment(fx.Root, "attachments/keep.bin", new byte[] { 9 });
        var delta = fx.Notes.CreateNote("delta");
        delta.BodyPlain = "post_snapshot_delta";
        fx.Notes.UpdateNote(delta);

        fx.Storage.StageRestore(dbSnap);
        var storage2 = new StorageService(fx.Paths);
        storage2.ApplyPendingRestoreIfAny();
        storage2.Initialize();
        var notes2 = new NoteService(storage2);

        Assert.NotEmpty(notes2.Search("unique_marker_gamma"));   // snapshot note restored
        Assert.Empty(notes2.Search("post_snapshot_delta"));      // later note gone (db replaced)
        Assert.True(File.Exists(keepAbs));                       // attachments untouched by db-only restore
    }

    [Fact]
    public void InspectBackup_rejects_a_non_backup_zip()
    {
        using var fx = new NotebookFixture();
        var junk = Path.Combine(fx.Root, "junk.zip");
        using (var z = ZipFile.Open(junk, ZipArchiveMode.Create))
            z.CreateEntry("readme.txt");
        var (ok, msg) = fx.Storage.InspectBackup(junk);
        Assert.False(ok);
        Assert.Contains("notebook.db", msg);
    }
}
