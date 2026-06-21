using System.ComponentModel;
using Microsoft.UI.Xaml;
using MyNotebook.Core.Models;

namespace MyNotebook.App;

/// <summary>One row in the note-list pane. Observable so live edits (title, pin) refresh in place.</summary>
public sealed class NoteListRow : INotifyPropertyChanged
{
    public long Id { get; init; }
    public NoteType Type { get; init; }

    private bool _pinned;
    public bool Pinned { get => _pinned; set { if (_pinned == value) return; _pinned = value; Raise(nameof(PinVisibility)); } }

    private string _title = "";
    public string Title { get => _title; set { if (_title == value) return; _title = value; Raise(nameof(Title)); } }

    private string _subtitle = "";
    public string Subtitle { get => _subtitle; set { if (_subtitle == value) return; _subtitle = value; Raise(nameof(Subtitle)); } }

    // Segoe Fluent glyphs: Pictures (thread) / Page (note).
    public string Glyph => Type == NoteType.Thread ? "\uE8B9" : "\uE7C3";
    public Visibility PinVisibility => Pinned ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>One row in the Timeline list — a note or thread at a point in time.</summary>
public sealed class TimelineRow
{
    public long NoteId { get; init; }
    public NoteType Type { get; init; }
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public string TimeText { get; init; } = "";

    /// <summary>Emoji marker matching the sidebar's note/thread icons.</summary>
    public string Glyph => Type == NoteType.Thread ? "\U0001F5BC" : "\U0001F4DD";

    /// <summary>Hide the preview line when there's nothing to show under the title.</summary>
    public Visibility PreviewVisibility =>
        string.IsNullOrEmpty(Preview) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A date bucket of timeline rows; <see cref="Key"/> is the section header text.</summary>
public sealed class TimelineGroup : List<TimelineRow>
{
    public string Key { get; init; } = "";
}
