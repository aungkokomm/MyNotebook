using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

/// <summary>
/// A throwaway notebook on a temp data root. Each test gets a fresh DB so they
/// are isolated and order-independent.
/// </summary>
public sealed class NotebookFixture : IDisposable
{
    public string Root { get; }
    public IPathService Paths { get; }
    public IStorageService Storage { get; }
    public NoteService Notes { get; }

    public NotebookFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "MyNotebookTests", Guid.NewGuid().ToString("N"));
        Paths = new PathService(explicitRoot: Root);
        Storage = new StorageService(Paths);
        Storage.Initialize();
        Notes = new NoteService(Storage);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

public class SchemaTests
{
    [Fact]
    public void Initialize_sets_schema_version_to_current()
    {
        using var fx = new NotebookFixture();
        Assert.Equal(6, fx.Storage.SchemaVersion);
    }

    [Fact]
    public void Initialize_is_idempotent()
    {
        using var fx = new NotebookFixture();
        fx.Storage.Initialize(); // second call must not throw or duplicate
        Assert.Equal(6, fx.Storage.SchemaVersion);
    }

    [Fact]
    public void Migration_seeds_a_default_notebook()
    {
        using var fx = new NotebookFixture();
        var nbs = fx.Notes.ListNotebooks();
        Assert.Single(nbs);
        Assert.Equal("My Notebook", nbs[0].Name);
    }
}

public class NotebookTests
{
    [Fact]
    public void Notes_and_folders_are_scoped_to_their_notebook()
    {
        using var fx = new NotebookFixture();
        var chem = fx.Notes.CreateNotebook("Chemistry");
        var phys = fx.Notes.CreateNotebook("Physics");

        var acids = fx.Notes.CreateFolder("Acids", notebookId: chem.Id);
        fx.Notes.CreateNote("Titration", NoteType.Note, acids.Id, chem.Id);
        fx.Notes.CreateNote("Loose chem note", NoteType.Note, null, chem.Id);
        fx.Notes.CreateNote("Kinematics", NoteType.Note, null, phys.Id);

        Assert.Single(fx.Notes.ListFolders(chem.Id));
        Assert.Empty(fx.Notes.ListFolders(phys.Id));
        Assert.Equal(2, fx.Notes.ListNotes(notebookId: chem.Id).Count);
        Assert.Single(fx.Notes.ListNotes(notebookId: phys.Id));

        // A note created in a folder inherits that folder's notebook.
        var inFolder = fx.Notes.ListNotes(acids.Id).Single();
        Assert.Equal(chem.Id, inFolder.NotebookId);
    }

    [Fact]
    public void Deleting_a_notebook_trashes_its_notes_and_folders()
    {
        using var fx = new NotebookFixture();
        var chem = fx.Notes.CreateNotebook("Chemistry");
        var f = fx.Notes.CreateFolder("Section", notebookId: chem.Id);
        fx.Notes.CreateNote("Note A", NoteType.Note, f.Id, chem.Id);

        Assert.Equal(1, fx.Notes.NotebookNoteCount(chem.Id));
        fx.Notes.DeleteNotebook(chem.Id);

        Assert.DoesNotContain(fx.Notes.ListNotebooks(), n => n.Id == chem.Id);
        Assert.Empty(fx.Notes.ListFolders(chem.Id));
        Assert.Equal(0, fx.Notes.NotebookNoteCount(chem.Id));
        Assert.Contains(fx.Notes.ListTrash(), n => n.Title == "Note A");
    }

    [Fact]
    public void Moving_a_note_into_a_folder_rehomes_it_to_that_notebook()
    {
        using var fx = new NotebookFixture();
        var chem = fx.Notes.CreateNotebook("Chemistry");
        var phys = fx.Notes.CreateNotebook("Physics");
        var physFolder = fx.Notes.CreateFolder("Mechanics", notebookId: phys.Id);
        var note = fx.Notes.CreateNote("Wandering note", NoteType.Note, null, chem.Id);

        fx.Notes.MoveNoteToFolder(note.Id, physFolder.Id);

        var moved = fx.Notes.GetNote(note.Id)!;
        Assert.Equal(physFolder.Id, moved.FolderId);
        Assert.Equal(phys.Id, moved.NotebookId);
    }
}

public class TimelineTests
{
    [Fact]
    public void Timeline_excludes_deleted_and_returns_live_notes()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.CreateNote("A");
        var b = fx.Notes.CreateNote("B");
        fx.Notes.SoftDeleteNote(b.Id);

        var tl = fx.Notes.ListTimeline();
        Assert.Contains(tl, x => x.Id == a.Id);
        Assert.DoesNotContain(tl, x => x.Id == b.Id);
    }

    [Fact]
    public void Timeline_modified_axis_orders_by_last_edit_desc()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.CreateNote("A");
        Thread.Sleep(8);
        fx.Notes.CreateNote("B");
        Thread.Sleep(8);
        fx.Notes.CreateNote("C");
        Thread.Sleep(8);
        a.BodyPlain = "touched"; fx.Notes.UpdateNote(a);   // edit A last

        var tl = fx.Notes.ListTimeline(TimelineAxis.Modified);
        Assert.Equal(a.Id, tl[0].Id);                      // most recently edited first
    }

    [Fact]
    public void Timeline_created_axis_ignores_later_edits()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.CreateNote("A");
        Thread.Sleep(8);
        fx.Notes.CreateNote("B");
        Thread.Sleep(8);
        var c = fx.Notes.CreateNote("C");
        Thread.Sleep(8);
        a.BodyPlain = "touched"; fx.Notes.UpdateNote(a);   // editing A must not reorder

        var tl = fx.Notes.ListTimeline(TimelineAxis.Created);
        Assert.Equal(c.Id, tl[0].Id);                      // newest creation first
        Assert.Equal(a.Id, tl[^1].Id);                     // oldest creation last
    }
}

public class NoteCrudTests
{
    [Fact]
    public void Create_then_get_roundtrips()
    {
        using var fx = new NotebookFixture();
        var created = fx.Notes.CreateNote("Hello", NoteType.Note);
        var fetched = fx.Notes.GetNote(created.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Hello", fetched!.Title);
        Assert.Equal(NoteType.Note, fetched.Type);
        Assert.False(fetched.Deleted);
    }

    [Fact]
    public void Update_persists_body_and_pin()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Draft");
        n.Title = "Final";
        n.BodyRtf = @"{\rtf1 hello}";
        n.BodyPlain = "hello world";
        n.Pinned = true;
        fx.Notes.UpdateNote(n);

        var got = fx.Notes.GetNote(n.Id)!;
        Assert.Equal("Final", got.Title);
        Assert.Equal("hello world", got.BodyPlain);
        Assert.True(got.Pinned);
    }

    [Fact]
    public void ListNotes_excludes_soft_deleted_and_pins_first()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.CreateNote("A");
        var b = fx.Notes.CreateNote("B");
        var c = fx.Notes.CreateNote("C");
        fx.Notes.SetPinned(c.Id, true);
        fx.Notes.SoftDeleteNote(b.Id);

        var list = fx.Notes.ListNotes();
        Assert.DoesNotContain(list, x => x.Id == b.Id);     // soft-deleted hidden
        Assert.Equal(c.Id, list[0].Id);                     // pinned first
        Assert.Contains(list, x => x.Id == a.Id);
    }
}

public class SearchTests
{
    [Fact]
    public void Search_finds_note_by_body_text()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Meeting");
        n.BodyPlain = "discussed the quarterly roadmap";
        fx.Notes.UpdateNote(n);

        var hits = fx.Notes.Search("roadmap");
        Assert.Contains(hits, h => h.NoteId == n.Id);
    }

    [Fact]
    public void Search_finds_note_by_text_inside_image_ocr()
    {
        // OneNote's killer feature: the word exists ONLY in an image's OCR text.
        using var fx = new NotebookFixture();
        var thread = fx.Notes.CreateNote("Receipts", NoteType.Thread);
        fx.Notes.AddImage(thread.Id, "attachments/screenshots/1/a.png", 320, 240,
                          ocrText: "Café Luna total $42.50");

        var hits = fx.Notes.Search("42.50");
        Assert.Contains(hits, h => h.NoteId == thread.Id);
    }

    [Fact]
    public void Search_is_diacritic_folded()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Trip");
        n.BodyPlain = "lunch at the café";
        fx.Notes.UpdateNote(n);

        Assert.Contains(fx.Notes.Search("cafe"), h => h.NoteId == n.Id); // no accent typed
    }

    [Fact]
    public void Search_supports_prefix_as_you_type()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Auth");
        n.BodyPlain = "the login flow failed";
        fx.Notes.UpdateNote(n);

        Assert.Contains(fx.Notes.Search("log"), h => h.NoteId == n.Id);  // 'log' -> 'login'
    }

    [Fact]
    public void Soft_deleted_notes_are_excluded_from_search()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Secret");
        n.BodyPlain = "topsecret marker";
        fx.Notes.UpdateNote(n);
        fx.Notes.SoftDeleteNote(n.Id);

        Assert.Empty(fx.Notes.Search("topsecret"));
    }

    [Fact]
    public void Removing_ocr_text_updates_the_index()
    {
        using var fx = new NotebookFixture();
        var thread = fx.Notes.CreateNote("T", NoteType.Thread);
        var img = fx.Notes.AddImage(thread.Id, "attachments/screenshots/1/a.png", 10, 10,
                                    ocrText: "uniquemarker123");
        Assert.NotEmpty(fx.Notes.Search("uniquemarker123"));

        fx.Notes.UpdateImageOcr(img.Id, "");           // OCR re-run cleared it
        Assert.Empty(fx.Notes.Search("uniquemarker123"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("hello", "\"hello\"*")]
    [InlineData("login flow", "\"login\" \"flow\"*")]
    public void BuildMatchQuery_quotes_and_prefixes(string input, string expected)
    {
        Assert.Equal(expected, NoteService.BuildMatchQuery(input));
    }
}

public class PathServiceTests
{
    [Fact]
    public void Explicit_root_is_portable_and_created()
    {
        var root = Path.Combine(Path.GetTempPath(), "MNB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new PathService(explicitRoot: root);
            Assert.True(p.IsPortable);
            Assert.True(Directory.Exists(p.DataRoot));
            Assert.Equal(Path.Combine(root, "notebook.db"), p.DbPath);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Screenshot_rel_path_is_unique_and_resolves_under_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "MNB_" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new PathService(explicitRoot: root);
            var r1 = p.NewScreenshotRelPath(7);
            var r2 = p.NewScreenshotRelPath(7);
            Assert.NotEqual(r1, r2);                                  // collision-proof
            Assert.StartsWith("attachments/screenshots/7/", r1);
            Assert.StartsWith(p.DataRoot, p.ToAbsolute(r1));          // stays under root
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
