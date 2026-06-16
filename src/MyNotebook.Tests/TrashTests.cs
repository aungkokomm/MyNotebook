using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

public class TrashTests
{
    [Fact]
    public void SoftDeleted_note_appears_in_trash_and_not_in_list()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("doomed");
        fx.Notes.SoftDeleteNote(n.Id);

        Assert.DoesNotContain(fx.Notes.ListNotes(), x => x.Id == n.Id);
        Assert.Contains(fx.Notes.ListTrash(), x => x.Id == n.Id);
    }

    [Fact]
    public void RestoreNote_brings_it_back_and_reindexes_for_search()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("phoenix");
        n.BodyPlain = "risesagain marker";
        fx.Notes.UpdateNote(n);
        fx.Notes.SoftDeleteNote(n.Id);
        Assert.Empty(fx.Notes.Search("risesagain"));   // gone from search while trashed

        fx.Notes.RestoreNote(n.Id);
        Assert.Contains(fx.Notes.ListNotes(), x => x.Id == n.Id);
        Assert.DoesNotContain(fx.Notes.ListTrash(), x => x.Id == n.Id);
        Assert.NotEmpty(fx.Notes.Search("risesagain"));  // searchable again
    }

    [Fact]
    public void DeleteNoteForever_removes_row_and_returns_image_paths()
    {
        using var fx = new NotebookFixture();
        var t = fx.Notes.CreateNote("thread", MyNotebook.Core.Models.NoteType.Thread);
        fx.Notes.AddImage(t.Id, "attachments/screenshots/9/x.png", 10, 10);
        fx.Notes.SoftDeleteNote(t.Id);

        var paths = fx.Notes.DeleteNoteForever(t.Id);
        Assert.Contains("attachments/screenshots/9/x.png", paths);
        Assert.Null(fx.Notes.GetNote(t.Id));            // hard-deleted
        Assert.DoesNotContain(fx.Notes.ListTrash(), x => x.Id == t.Id);
    }
}
