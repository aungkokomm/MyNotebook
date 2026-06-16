using Microsoft.UI.Xaml;
using MyNotebook.Core.Models;

namespace MyNotebook.App;

/// <summary>One row in the Timeline list — a note or thread at a point in time.</summary>
public sealed class TimelineRow
{
    public long NoteId { get; init; }
    public NoteType Type { get; init; }
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public string TimeText { get; init; } = "";

    /// <summary>Emoji marker matching the sidebar's note/thread icons.</summary>
    public string Glyph => Type == NoteType.Thread ? "🖼" : "📝";

    /// <summary>Hide the preview line when there's nothing to show under the title.</summary>
    public Visibility PreviewVisibility =>
        string.IsNullOrEmpty(Preview) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A date bucket of timeline rows; <see cref="Key"/> is the section header text.</summary>
public sealed class TimelineGroup : List<TimelineRow>
{
    public string Key { get; init; } = "";
}
