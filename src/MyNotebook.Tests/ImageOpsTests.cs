using MyNotebook.Core.Models;
using Xunit;

namespace MyNotebook.Tests;

public class ImageOpsTests
{
    [Fact]
    public void DeleteImage_removes_it_and_returns_its_relpath()
    {
        using var fx = new NotebookFixture();
        var t = fx.Notes.CreateNote("thread", NoteType.Thread);
        var a = fx.Notes.AddImage(t.Id, "attachments/screenshots/1/a.png", 10, 10);
        var b = fx.Notes.AddImage(t.Id, "attachments/screenshots/1/b.png", 10, 10);

        var rel = fx.Notes.DeleteImage(a.Id);

        Assert.Equal("attachments/screenshots/1/a.png", rel);
        var left = fx.Notes.ListImages(t.Id);
        Assert.Single(left);
        Assert.Equal(b.Id, left[0].Id);
    }

    [Fact]
    public void ReorderImages_persists_the_new_order()
    {
        using var fx = new NotebookFixture();
        var t = fx.Notes.CreateNote("thread", NoteType.Thread);
        var a = fx.Notes.AddImage(t.Id, "a.png", 10, 10);
        var b = fx.Notes.AddImage(t.Id, "b.png", 10, 10);
        var c = fx.Notes.AddImage(t.Id, "c.png", 10, 10);

        // New order: c, a, b
        fx.Notes.ReorderImages(t.Id, new[] { c.Id, a.Id, b.Id });

        var ids = fx.Notes.ListImages(t.Id).Select(x => x.Id).ToList();
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, ids);
    }
}
