using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyNotebook.Core.Services;

public enum AppTheme { System, Light, Dark }
public enum AppBackdrop { Mica, Acrylic, Solid }
public enum PaperPattern { Ruled, Grid, Dotted, Notebook }

/// <summary>User-facing preferences. Persisted as Data/settings.json (portable).</summary>
public sealed class AppSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppBackdrop Backdrop { get; set; } = AppBackdrop.Mica;
    public double EditorFontSize { get; set; } = 15;
    public bool ConfirmBeforeDelete { get; set; } = true;
    public bool SpellCheck { get; set; } = true;
    public bool QuickNoteHotkey { get; set; } = false;
    public double SidebarWidth { get; set; } = 260;
    public double RailWidth { get; set; } = 194;
    /// <summary>X button hides to tray (keep running) instead of quitting.</summary>
    public bool CloseToTray { get; set; } = true;

    // Window placement (remembered across runs). Width 0 = "not saved yet".
    public int WindowX { get; set; } = 0;
    public int WindowY { get; set; } = 0;
    public int WindowWidth { get; set; } = 0;
    public int WindowHeight { get; set; } = 0;
    public bool WindowMaximized { get; set; } = false;

    // Note paper
    public bool PaperLines { get; set; } = false;
    public PaperPattern PaperType { get; set; } = PaperPattern.Ruled;
    /// <summary>Page background as #RRGGBB, or "" to follow the theme (white/dark).</summary>
    public string PageColor { get; set; } = "";

    /// <summary>App accent color as #RRGGBB, or "" to follow the Windows accent.</summary>
    public string AccentColor { get; set; } = "";

    /// <summary>How strongly the theme tints the surfaces. 0 = none, 1 = default, up to 2.</summary>
    public double ThemeIntensity { get; set; } = 1.0;

    /// <summary>When pasting text copied from a web page, append the source URL (OneNote-style).</summary>
    public bool PasteSourceUrl { get; set; } = true;

    /// <summary>UI language code ("en", "pt", …), or "" to follow the Windows display language.</summary>
    public string Language { get; set; } = "";

    /// <summary>Id of the notebook shown on last launch, or 0 to fall back to the first notebook.</summary>
    public long CurrentNotebookId { get; set; } = 0;
}

public interface ISettingsService
{
    AppSettings Current { get; }
    void Save();
    /// <summary>Raised after Save so the UI can re-apply theme/backdrop/etc.</summary>
    event Action? Changed;
}

/// <summary>JSON-backed settings stored next to the database in the portable data root.</summary>
public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    public SettingsService(IPathService paths)
    {
        _path = Path.Combine(paths.DataRoot, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new();
        }
        catch
        {
            Current = new(); // corrupt file -> sane defaults
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Options));
        }
        catch { /* best effort; settings are non-critical */ }
        Changed?.Invoke();
    }
}
