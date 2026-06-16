using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

public class FolderOpsTests
{
    [Fact]
    public void RenameFolder_changes_name()
    {
        using var fx = new NotebookFixture();
        var f = fx.Notes.CreateFolder("Old");
        fx.Notes.RenameFolder(f.Id, "New");
        Assert.Contains(fx.Notes.ListFolders(), x => x.Id == f.Id && x.Name == "New");
    }

    [Fact]
    public void MoveNoteToFolder_reparents_and_unfiles()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.CreateFolder("A");
        var n = fx.Notes.CreateNote("note");
        fx.Notes.MoveNoteToFolder(n.Id, a.Id);
        Assert.Equal(a.Id, fx.Notes.GetNote(n.Id)!.FolderId);
        fx.Notes.MoveNoteToFolder(n.Id, null);
        Assert.Null(fx.Notes.GetNote(n.Id)!.FolderId);
    }

    [Fact]
    public void DeleteFolder_soft_deletes_folder_subfolders_and_notes()
    {
        using var fx = new NotebookFixture();
        var parent = fx.Notes.CreateFolder("Parent");
        var child = fx.Notes.CreateFolder("Child", parent.Id);
        var n1 = fx.Notes.CreateNote("in parent");
        var n2 = fx.Notes.CreateNote("in child");
        fx.Notes.MoveNoteToFolder(n1.Id, parent.Id);
        fx.Notes.MoveNoteToFolder(n2.Id, child.Id);

        fx.Notes.DeleteFolder(parent.Id);

        // Folder + subfolder gone from the list; notes soft-deleted.
        Assert.DoesNotContain(fx.Notes.ListFolders(), x => x.Id == parent.Id || x.Id == child.Id);
        Assert.True(fx.Notes.GetNote(n1.Id)!.Deleted);
        Assert.True(fx.Notes.GetNote(n2.Id)!.Deleted);
        Assert.DoesNotContain(fx.Notes.ListNotes(), x => x.Id == n1.Id || x.Id == n2.Id);
        Assert.Equal(2, fx.Notes.GetStats().DeletedNotes);
    }
}
