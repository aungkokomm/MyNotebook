using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MyNotebook.Core.Models;
using MyNotebook.Core.Services;
using System.Diagnostics;
using System.Text;
using Windows.Storage.Pickers;

namespace MyNotebook.App;

/// <summary>
/// Standalone, resizable Settings window. Edits ISettingsService and asks the owning
/// MainWindow to apply each change live (so the preview updates while this window is open).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly ISettingsService _settings;
    private readonly INoteService _notes;
    private readonly IPathService _paths;
    private readonly IOcrService _ocr;
    private bool _loading;

    public SettingsWindow(MainWindow main, ISettingsService settings, INoteService notes, IPathService paths, IOcrService ocr)
    {
        _main = main;
        _settings = settings;
        _notes = notes;
        _paths = paths;
        _ocr = ocr;

        _loading = true;          // suppress control-init coercion (slider Min etc.)
        InitializeComponent();

        Title = "Settings — My Notebook";
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        aw.Resize(new Windows.Graphics.SizeInt32(640, 760));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon)) aw.SetIcon(icon);

        BuildPageColorSwatches();
        BuildAccentThemeSwatches();
        Load();
        _loading = false;
    }

    private void Load()
    {
        SelectComboByTag(ThemeCombo, _settings.Current.Theme.ToString());
        SelectComboByTag(BackdropCombo, _settings.Current.Backdrop.ToString());
        FontSizeSlider.Value = _settings.Current.EditorFontSize;
        IntensitySlider.Value = Math.Clamp(_settings.Current.ThemeIntensity * 100.0, 0, 200);
        ConfirmDeleteToggle.IsOn = _settings.Current.ConfirmBeforeDelete;
        SpellCheckToggle.IsOn = _settings.Current.SpellCheck;
        CloseToTrayToggle.IsOn = _settings.Current.CloseToTray;
        QuickNoteToggle.IsOn = _settings.Current.QuickNoteHotkey;
        PasteSourceToggle.IsOn = _settings.Current.PasteSourceUrl;
        PaperLinesToggle.IsOn = _settings.Current.PaperLines;
        SelectComboByTag(PaperTypeCombo, _settings.Current.PaperType.ToString());
        UpdateSwatchSelection();
        UpdateAccentSelection();

        RefreshStats();
        DataPathText.Text = (_paths.IsPortable ? "Portable (next to the app): " : "User data folder: ") + _paths.DataRoot;
        OcrStatusText.Text = _ocr.IsAvailable
            ? $"OCR engine ready — language: {_ocr.EngineLanguage}. Text inside screenshots is searchable."
            : "No OCR language pack found. Install one in Windows Settings → Time & language → Language to make screenshot text searchable.";
        RerunOcrButton.IsEnabled = _ocr.IsAvailable;
        AboutText.Text = $"My Notebook {GetAppVersion()} — local-first notes with OCR-searchable screenshots.";
    }

    // ----------------------------------------------------------- Appearance
    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.Theme = Enum.Parse<AppTheme>(SelectedTag(ThemeCombo));
        _settings.Save();
        _main.ApplyThemeLive();
        BuildPageColorSwatches();   // "Follow theme" swatch + selection reflect the new theme
    }

    private void BackdropCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.Backdrop = Enum.Parse<AppBackdrop>(SelectedTag(BackdropCombo));
        _settings.Save();
        _main.ApplyBackdropLive();
    }

    private void FontSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.EditorFontSize = e.NewValue;
        _settings.Save();
        _main.ApplyFontLive();
    }

    private void IntensitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.ThemeIntensity = e.NewValue / 100.0;
        _settings.Save();
        _main.ApplyTintLive();
    }

    // ----------------------------------------------------------- Note paper
    private void PaperLinesToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.PaperLines = PaperLinesToggle.IsOn;
        _settings.Save();
        _main.ApplyPaperLive();
    }

    private void PaperTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.PaperType = Enum.Parse<PaperPattern>(SelectedTag(PaperTypeCombo));
        _settings.Save();
        _main.ApplyPaperLive();
    }

    private static readonly (string Name, string Hex)[] PageColorPalette =
    {
        ("Follow theme", ""), ("White", "#FFFFFF"), ("Cream", "#FBF0D9"),
        ("Sepia", "#F4ECD8"), ("Mint", "#E8F5E9"), ("Sky", "#E3F2FD"),
        ("Rose", "#FCE4EC"), ("Slate", "#2B2B2B"),
    };

    private void BuildPageColorSwatches()
    {
        PageColorList.Items.Clear();
        foreach (var (name, hex) in PageColorPalette)
        {
            var btn = new Button
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Padding = new Thickness(0),
                Background = new SolidColorBrush(SwatchColor(hex)), BorderThickness = new Thickness(2), Tag = hex,
            };
            ToolTipService.SetToolTip(btn, name);
            btn.Click += (_, _) =>
            {
                _settings.Current.PageColor = hex;
                _settings.Save();
                _main.ApplyPaperLive();
                UpdateSwatchSelection();
            };
            PageColorList.Items.Add(btn);
        }
        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        foreach (var obj in PageColorList.Items)
            if (obj is Button b)
                b.BorderBrush = (string)b.Tag == _settings.Current.PageColor
                    ? new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue)
                    : (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
    }

    private Windows.UI.Color SwatchColor(string hex) =>
        hex.Length == 0
            ? (IsDarkMode() ? Windows.UI.Color.FromArgb(255, 39, 39, 39) : Microsoft.UI.Colors.White)
            : ParseHex(hex);

    private static readonly (string Name, string Accent, string Page)[] ColorThemes =
    {
        ("Follow Windows", "",        ""),
        ("Dark blue",      "#1E4D8B", "#EAF1FB"),
        ("Coffee brown",   "#6B4A33", "#F4EDE3"),
        ("Light pink",     "#B0436A", "#FBEEF3"),
        ("Dark green",     "#2A6048", "#E8F3EC"),
        ("Plum",           "#6A4A8F", "#F2ECFA"),
        ("Teal",           "#0F6E63", "#E4F3F0"),
        ("Slate",          "#45556B", "#EDF0F3"),
    };

    private void BuildAccentThemeSwatches()
    {
        AccentThemeList.Items.Clear();
        foreach (var (name, accent, page) in ColorThemes)
        {
            var dot = accent.Length == 0 ? SystemAccentColorValue() : ParseHex(accent);
            var btn = new Button
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Padding = new Thickness(0),
                Background = new SolidColorBrush(dot), BorderThickness = new Thickness(2), Tag = accent,
            };
            ToolTipService.SetToolTip(btn, name);
            btn.Click += (_, _) =>
            {
                _settings.Current.AccentColor = accent;
                _settings.Current.PageColor = page;
                _settings.Save();
                _main.ApplyAccentTheme();
                UpdateAccentSelection();
                UpdateSwatchSelection();
            };
            AccentThemeList.Items.Add(btn);
        }
        UpdateAccentSelection();
    }

    private void UpdateAccentSelection()
    {
        foreach (var obj in AccentThemeList.Items)
            if (obj is Button b)
                b.BorderBrush = (string)b.Tag == _settings.Current.AccentColor
                    ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                    : (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
    }

    private static Windows.UI.Color SystemAccentColorValue()
    {
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
        catch { return Microsoft.UI.Colors.SlateGray; }
    }

    // ----------------------------------------------------------- Behavior
    private void ConfirmDeleteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.ConfirmBeforeDelete = ConfirmDeleteToggle.IsOn;
        _settings.Save();
    }

    private void SpellCheckToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.SpellCheck = SpellCheckToggle.IsOn;
        _settings.Save();
        _main.ApplySpellLive();
    }

    private void CloseToTrayToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.CloseToTray = CloseToTrayToggle.IsOn;
        _settings.Save();
    }

    private void PasteSourceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.Current.PasteSourceUrl = PasteSourceToggle.IsOn;
        _settings.Save();
    }

    private async void QuickNoteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ok = _main.SetQuickNoteHotkey(QuickNoteToggle.IsOn);
        _settings.Current.QuickNoteHotkey = QuickNoteToggle.IsOn && ok;
        _settings.Save();
        if (QuickNoteToggle.IsOn && !ok)
            await ShowInfoAsync("Hotkey unavailable", "Win+Alt+N could not be registered (another app may be using it).");
    }

    // ----------------------------------------------------------- Data & OCR
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.DataRoot}\"") { UseShellExecute = true }); }
        catch { }
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Empty trash?",
            Content = "Permanently delete all notes in the trash and their screenshots. This cannot be undone.",
            PrimaryButtonText = "Empty trash",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        foreach (var rel in _notes.EmptyTrash())
        {
            try { System.IO.File.Delete(_paths.ToAbsolute(rel)); } catch { }
        }
        RefreshStats();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Zip archive", new List<string> { ".zip" });
        picker.SuggestedFileName = $"MyNotebook-backup-{DateTime.Now:yyyyMMdd-HHmm}";
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        try
        {
            var path = file.Path;
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.Compression.ZipFile.CreateFromDirectory(
                _paths.DataRoot, path, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            await ShowInfoAsync("Backup complete", $"A full copy of your notebook was saved to:\n{path}");
        }
        catch (Exception ex) { await ShowInfoAsync("Backup failed", ex.Message); }
    }

    private async void ExportAll_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        int n = 0;
        foreach (var note in _notes.ListNotes())
        {
            var safe = string.Join("_", (string.IsNullOrWhiteSpace(note.Title) ? "untitled" : note.Title)
                                        .Split(System.IO.Path.GetInvalidFileNameChars()));
            var sb = new StringBuilder();
            sb.AppendLine($"# {note.Title}").AppendLine();
            if (note.Type == NoteType.Thread)
                foreach (var img in _notes.ListImages(note.Id))
                {
                    sb.AppendLine($"![{img.Caption}]({img.RelPath})");
                    if (!string.IsNullOrWhiteSpace(img.OcrText)) sb.AppendLine().AppendLine($"> {img.OcrText}");
                    sb.AppendLine();
                }
            else sb.AppendLine(note.BodyPlain);

            try { await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, $"{safe}_{note.Id}.md"), sb.ToString()); n++; }
            catch { }
        }
        await ShowInfoAsync("Export complete", $"Exported {n} notes to:\n{folder.Path}");
    }

    private async void RerunOcr_Click(object sender, RoutedEventArgs e)
    {
        if (!_ocr.IsAvailable) return;
        var images = _notes.AllImages();
        MaintProgress.Visibility = Visibility.Visible;
        MaintProgress.Maximum = Math.Max(1, images.Count);
        MaintProgress.Value = 0;
        RerunOcrButton.IsEnabled = false;
        foreach (var img in images)
        {
            try { _notes.UpdateImageOcr(img.Id, await _ocr.RecognizeAsync(_paths.ToAbsolute(img.RelPath))); }
            catch { }
            MaintProgress.Value += 1;
        }
        MaintProgress.Visibility = Visibility.Collapsed;
        RerunOcrButton.IsEnabled = true;
        await ShowInfoAsync("OCR complete", $"Re-processed {images.Count} images. Screenshot text is up to date in search.");
    }

    private async void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        _notes.RebuildSearchIndex();
        await ShowInfoAsync("Search index rebuilt", "The full-text search index was rebuilt from your notes.");
    }

    // ----------------------------------------------------------- About
    private AboutWindow? _aboutWindow;

    private void About_Click(object sender, RoutedEventArgs e)
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }
        _aboutWindow.Activate();
    }

    // ----------------------------------------------------------- Helpers
    private void RefreshStats()
    {
        var s = _notes.GetStats();
        StatsText.Text = $"{s.Notes} notes · {s.Threads} screenshot threads · {s.Images} images · {s.DeletedNotes} in trash.";
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
            if (obj is ComboBoxItem cbi && (string)cbi.Tag == tag) { combo.SelectedItem = cbi; return; }
    }

    private static string SelectedTag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private bool IsDarkMode() =>
        _settings.Current.Theme == AppTheme.Dark ||
        (_settings.Current.Theme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    private void InitializeWithWindow(object target) =>
        WinRT.Interop.InitializeWithWindow.Initialize(target, WinRT.Interop.WindowNative.GetWindowHandle(this));

    private Task ShowInfoAsync(string title, string message) =>
        new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot }
            .ShowAsync().AsTask();

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
