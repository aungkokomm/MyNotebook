using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

public class MaintenanceTests
{
    [Fact]
    public void GetStats_counts_notes_threads_images_and_trash()
    {
        using var fx = new NotebookFixture();
        fx.Notes.CreateNote("a");
        var t = fx.Notes.CreateNote("thread", NoteType.Thread);
        fx.Notes.AddImage(t.Id, "attachments/screenshots/1/a.png", 10, 10);
        var gone = fx.Notes.CreateNote("trash me");
        fx.Notes.SoftDeleteNote(gone.Id);

        var s = fx.Notes.GetStats();
        Assert.Equal(1, s.Notes);       // 'a' (the deleted one is excluded)
        Assert.Equal(1, s.Threads);
        Assert.Equal(1, s.Images);
        Assert.Equal(1, s.DeletedNotes);
    }

    [Fact]
    public void EmptyTrash_purges_deleted_notes_and_returns_image_paths()
    {
        using var fx = new NotebookFixture();
        var keep = fx.Notes.CreateNote("keep");
        var t = fx.Notes.CreateNote("thread", NoteType.Thread);
        fx.Notes.AddImage(t.Id, "attachments/screenshots/2/x.png", 10, 10);
        fx.Notes.SoftDeleteNote(t.Id);

        var purgedFiles = fx.Notes.EmptyTrash();
        Assert.Contains("attachments/screenshots/2/x.png", purgedFiles);

        // Deleted note (and its image rows) are gone; the kept note remains.
        Assert.Null(fx.Notes.GetNote(t.Id));
        Assert.NotNull(fx.Notes.GetNote(keep.Id));
        Assert.Equal(0, fx.Notes.GetStats().DeletedNotes);
    }

    [Fact]
    public void RebuildSearchIndex_reproduces_searchable_content()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Doc");
        n.BodyPlain = "rebuildable marker";
        fx.Notes.UpdateNote(n);
        var t = fx.Notes.CreateNote("T", NoteType.Thread);
        fx.Notes.AddImage(t.Id, "p.png", 1, 1, ocrText: "ocrmarker");

        fx.Notes.RebuildSearchIndex();

        Assert.Contains(fx.Notes.Search("rebuildable"), h => h.NoteId == n.Id);
        Assert.Contains(fx.Notes.Search("ocrmarker"), h => h.NoteId == t.Id);
        // Soft-deleted notes are not re-indexed.
        fx.Notes.SoftDeleteNote(n.Id);
        fx.Notes.RebuildSearchIndex();
        Assert.Empty(fx.Notes.Search("rebuildable"));
    }

    [Fact]
    public void Settings_roundtrip_persists_to_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "MNB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new PathService(explicitRoot: root);
            var s1 = new SettingsService(paths);
            Assert.Equal(AppTheme.System, s1.Current.Theme);   // default
            s1.Current.Theme = AppTheme.Dark;
            s1.Current.EditorFontSize = 18;
            s1.Save();

            // New instance reads the persisted file.
            var s2 = new SettingsService(paths);
            Assert.Equal(AppTheme.Dark, s2.Current.Theme);
            Assert.Equal(18, s2.Current.EditorFontSize);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
