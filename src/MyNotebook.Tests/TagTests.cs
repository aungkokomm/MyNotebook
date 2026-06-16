using MyNotebook.Core.Services;
using Xunit;

namespace MyNotebook.Tests;

public class TagTests
{
    [Fact]
    public void Add_list_remove_tags_on_note()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("Doc");
        var idea = fx.Notes.EnsureTag("idea");
        var todo = fx.Notes.EnsureTag("todo");
        fx.Notes.AddTagToNote(n.Id, idea.Id);
        fx.Notes.AddTagToNote(n.Id, todo.Id);

        var tags = fx.Notes.ListTagsForNote(n.Id);
        Assert.Equal(2, tags.Count);
        Assert.Contains(tags, t => t.Name == "idea");

        fx.Notes.RemoveTagFromNote(n.Id, todo.Id);
        Assert.Single(fx.Notes.ListTagsForNote(n.Id));
    }

    [Fact]
    public void EnsureTag_is_idempotent_and_ListTags_returns_all()
    {
        using var fx = new NotebookFixture();
        var a = fx.Notes.EnsureTag("work");
        var b = fx.Notes.EnsureTag("work");   // same name -> same row
        Assert.Equal(a.Id, b.Id);
        Assert.Contains(fx.Notes.ListTags(), t => t.Name == "work");
    }

    [Fact]
    public void ListNotesWithTag_returns_tagged_notes_only()
    {
        using var fx = new NotebookFixture();
        var n1 = fx.Notes.CreateNote("tagged");
        var n2 = fx.Notes.CreateNote("untagged");
        var t = fx.Notes.EnsureTag("important");
        fx.Notes.AddTagToNote(n1.Id, t.Id);

        var hits = fx.Notes.ListNotesWithTag(t.Id);
        Assert.Contains(hits, x => x.Id == n1.Id);
        Assert.DoesNotContain(hits, x => x.Id == n2.Id);
    }
}
