namespace MyNotebook.Core.Models;

/// <summary>A note is either a rich-text document or a screenshot thread.</summary>
public enum NoteType
{
    Note,
    Thread
}

/// <summary>Which timestamp the Timeline view orders and groups notes by.</summary>
public enum TimelineAxis
{
    /// <summary>By last edit — a "recent activity" feed.</summary>
    Modified,
    /// <summary>By creation date — a journal view.</summary>
    Created
}

public static class NoteTypeExtensions
{
    public static string ToDbValue(this NoteType t) => t == NoteType.Thread ? "thread" : "note";
    public static NoteType ParseNoteType(string? s) =>
        string.Equals(s, "thread", StringComparison.OrdinalIgnoreCase) ? NoteType.Thread : NoteType.Note;
}

/// <summary>A top-level notebook (OneNote-style): the container above folders and notes.</summary>
public sealed class Notebook
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    /// <summary>Accent dot as #RRGGBB, or "" for none.</summary>
    public string Color { get; set; } = "";
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

public sealed class Folder
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public long? ParentId { get; set; }
    /// <summary>Which notebook this folder belongs to.</summary>
    public long? NotebookId { get; set; }
    /// <summary>Accent color as #RRGGBB, or "" to follow the theme (OneNote-style section color).</summary>
    public string Color { get; set; } = "";
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

public sealed class Note
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public long? FolderId { get; set; }
    /// <summary>Which notebook this note belongs to (independent of folder, so unfiled notes still have a home).</summary>
    public long? NotebookId { get; set; }
    public string Title { get; set; } = "";
    /// <summary>RichEditBox RTF — source of truth for formatting.</summary>
    public string BodyRtf { get; set; } = "";
    /// <summary>Plain text derived from RTF on save — the only thing fed to FTS.</summary>
    public string BodyPlain { get; set; } = "";
    public NoteType Type { get; set; } = NoteType.Note;
    public bool Pinned { get; set; }
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

public sealed class ImageItem
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public long NoteId { get; set; }
    /// <summary>Path relative to the data root, e.g. attachments/screenshots/{noteId}/{ts}_{short}.png</summary>
    public string RelPath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string OcrText { get; set; } = "";
    public string Caption { get; set; } = "";
    public int SortOrder { get; set; }
    public long CreatedAt { get; set; }
    public long UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>An auto-saved snapshot of a note's title + body at a point in time.</summary>
public sealed class NoteVersion
{
    public long Id { get; set; }
    public long NoteId { get; set; }
    public string Title { get; set; } = "";
    public string BodyRtf { get; set; } = "";
    public string BodyPlain { get; set; } = "";
    public long CreatedAt { get; set; }
}

public sealed class Tag
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "";
    public long CreatedAt { get; set; }
}

public sealed class SavedSearch
{
    public long Id { get; set; }
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "fts";
    public string Query { get; set; } = "";
    public string Icon { get; set; } = "";
    public int SortOrder { get; set; }
}

/// <summary>Aggregate counts shown on the Settings → Data page.</summary>
public sealed class NotebookStats
{
    public int Notes { get; set; }
    public int Threads { get; set; }
    public int Images { get; set; }
    public int DeletedNotes { get; set; }
}

/// <summary>A ranked search result row.</summary>
public sealed class SearchHit
{
    public long NoteId { get; set; }
    public string Title { get; set; } = "";
    public NoteType Type { get; set; }
    /// <summary>FTS snippet with match markers, drawn from the body column.</summary>
    public string Preview { get; set; } = "";
    /// <summary>bm25 rank — lower is more relevant.</summary>
    public double Rank { get; set; }
    public string Folder { get; set; } = "";
    public long Updated { get; set; }
}
