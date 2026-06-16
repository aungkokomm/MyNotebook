using Xunit;

namespace MyNotebook.Tests;

public class HybridSearchTests
{
    private static MyNotebook.Core.Models.Note NoteWithBody(NotebookFixture fx, string title, string body)
    {
        var n = fx.Notes.CreateNote(title);
        n.BodyPlain = body;
        fx.Notes.UpdateNote(n);
        return n;
    }

    [Fact]
    public void English_word_search_finds_the_note()
    {
        using var fx = new NotebookFixture();
        var n = NoteWithBody(fx, "Meeting", "Discuss the quarterly budget review");
        Assert.Contains(fx.Notes.Search("budget"), h => h.NoteId == n.Id);
    }

    [Fact]
    public void Myanmar_substring_in_a_spaceless_run_is_found()
    {
        using var fx = new NotebookFixture();
        // A run with no spaces; the search term sits in the MIDDLE — unicode61 alone
        // would tokenize the whole run as one token and miss it; trigram catches it.
        var n = NoteWithBody(fx, "မြန်မာ", "ဘာသာဗေဒဝဏ္ဏဗေဒသဒ္ဒဗေဒ");
        var hits = fx.Notes.Search("ဝဏ္ဏဗေဒ");
        Assert.Contains(hits, h => h.NoteId == n.Id);
    }

    [Fact]
    public void Partial_word_substring_is_found_via_trigram()
    {
        using var fx = new NotebookFixture();
        var n = NoteWithBody(fx, "Linguistics", "phonology and morphology");
        // "nolog" is a mid-word substring — only the trigram pass can match it.
        Assert.Contains(fx.Notes.Search("nolog"), h => h.NoteId == n.Id);
    }

    [Fact]
    public void Results_are_deduped_across_both_indexes()
    {
        using var fx = new NotebookFixture();
        var n = NoteWithBody(fx, "Budget", "budget budget budget");
        var hits = fx.Notes.Search("budget");
        Assert.Single(hits, h => h.NoteId == n.Id);
    }

    [Fact]
    public void Exclude_operator_drops_matching_notes()
    {
        using var fx = new NotebookFixture();
        var keep = NoteWithBody(fx, "A", "quarterly budget review");
        var drop = NoteWithBody(fx, "B", "quarterly budget meeting");

        var hits = fx.Notes.Search("budget -meeting");
        Assert.Contains(hits, h => h.NoteId == keep.Id);
        Assert.DoesNotContain(hits, h => h.NoteId == drop.Id);
    }

    [Fact]
    public void Title_scope_only_matches_the_title_column()
    {
        using var fx = new NotebookFixture();
        var inTitle = NoteWithBody(fx, "Budget plan", "nothing relevant");
        var inBody = NoteWithBody(fx, "Random", "the budget is here");

        var hits = fx.Notes.Search("title:budget");
        Assert.Contains(hits, h => h.NoteId == inTitle.Id);
        Assert.DoesNotContain(hits, h => h.NoteId == inBody.Id);
    }

    [Fact]
    public void Type_filter_restricts_to_threads()
    {
        using var fx = new NotebookFixture();
        var note = NoteWithBody(fx, "budget note", "budget");
        var thread = fx.Notes.CreateNote("budget thread", MyNotebook.Core.Models.NoteType.Thread);

        var hits = fx.Notes.Search("budget type:thread");
        Assert.DoesNotContain(hits, h => h.NoteId == note.Id);
    }
}
