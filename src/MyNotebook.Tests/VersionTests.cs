using Xunit;

namespace MyNotebook.Tests;

public class VersionTests
{
    [Fact]
    public void SaveNoteVersion_lists_newest_first()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("draft");
        fx.Notes.SaveNoteVersion(n.Id, "draft", "<div>v1</div>", "v1");
        fx.Notes.SaveNoteVersion(n.Id, "draft", "<div>v2</div>", "v2");

        var versions = fx.Notes.ListNoteVersions(n.Id);
        Assert.Equal(2, versions.Count);
        Assert.Equal("v2", versions[0].BodyPlain);   // newest first
        Assert.Equal("v1", versions[1].BodyPlain);
    }

    [Fact]
    public void GetNoteVersion_round_trips_the_snapshot()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("draft");
        fx.Notes.SaveNoteVersion(n.Id, "My title", "<div>body</div>", "body");
        var id = fx.Notes.ListNoteVersions(n.Id)[0].Id;

        var v = fx.Notes.GetNoteVersion(id);
        Assert.NotNull(v);
        Assert.Equal("My title", v!.Title);
        Assert.Equal("<div>body</div>", v.BodyRtf);
    }

    [Fact]
    public void SaveNoteVersion_prunes_to_the_newest_50()
    {
        using var fx = new NotebookFixture();
        var n = fx.Notes.CreateNote("draft");
        for (int i = 0; i < 60; i++)
            fx.Notes.SaveNoteVersion(n.Id, "draft", $"<div>{i}</div>", i.ToString());

        var versions = fx.Notes.ListNoteVersions(n.Id);
        Assert.Equal(50, versions.Count);
        Assert.Equal("59", versions[0].BodyPlain);   // newest kept
    }
}
