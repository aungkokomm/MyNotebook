using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using MyNotebook.App.Services;
using MyNotebook.Core.Services;

namespace MyNotebook.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private Window? _window;

    public App()
    {
        // Catch anything that would otherwise be a silent stowed exception.
        UnhandledException += (_, e) =>
        {
            Log("UnhandledException: " + e.Message + "\n" + e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("DomainUnhandled: " + e.ExceptionObject);

        try
        {
            InitializeComponent();
            Services = ConfigureServices();
            Log("Startup OK");
        }
        catch (Exception ex)
        {
            Log("Startup FAILED: " + ex);
            throw;
        }
    }

    /// <summary>Composition root. Singletons because the DB and paths are app-wide.</summary>
    private static IServiceProvider ConfigureServices()
    {
        var sc = new ServiceCollection();

        sc.AddSingleton<IPathService>(_ => new PathService());
        sc.AddSingleton<IStorageService, StorageService>();
        sc.AddSingleton<INoteService, NoteService>();
        sc.AddSingleton<IOcrService, WindowsOcrService>();
        sc.AddSingleton<ISyncService, NullSyncService>();
        sc.AddSingleton<ISettingsService, SettingsService>();

        sc.AddTransient<MainWindow>();

        var provider = sc.BuildServiceProvider();

        // Create the DB and apply migrations before any UI touches it.
        var paths = provider.GetRequiredService<IPathService>();
        Log($"DataRoot={paths.DataRoot} (portable={paths.IsPortable})");
        var storage = provider.GetRequiredService<IStorageService>();
        storage.Initialize();
        Log("Storage initialized: " + paths.DbPath);
        storage.BackupOnLaunch();   // keep rolling timestamped snapshots in Data\Backups\
        Log("Backup-on-launch done");
        return provider;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Apply the saved accent before the window's controls load (Resources isn't
            // accessible yet in the App constructor — it throws there).
            ApplyAccent(Services.GetRequiredService<ISettingsService>().Current.AccentColor);
            _window = Services.GetRequiredService<MainWindow>();
            _window.Activate();
            Log("Window activated");
        }
        catch (Exception ex)
        {
            Log("OnLaunched FAILED: " + ex);
            throw;
        }
    }

    /// <summary>
    /// Override the app accent color. WinUI derives every accent brush from SystemAccentColor
    /// and its light/dark variants, so setting these seven resources re-colors the whole app.
    /// Pass "" to follow the Windows accent. Applied at startup; live changes also flip the
    /// root theme once (in MainWindow) to force the brushes to re-resolve.
    /// </summary>
    internal static void ApplyAccent(string? hex)
    {
        var app = Current;
        if (app is null) return;
        Windows.UI.Color baseColor;
        if (string.IsNullOrWhiteSpace(hex))
        {
            try { baseColor = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent); }
            catch { return; }
        }
        else
        {
            try { baseColor = ParseHex(hex); } catch { return; }
        }

        try
        {
            var r = app.Resources;
            r["SystemAccentColor"] = baseColor;
            r["SystemAccentColorLight1"] = Mix(baseColor, true, 0.18);
            r["SystemAccentColorLight2"] = Mix(baseColor, true, 0.36);
            r["SystemAccentColorLight3"] = Mix(baseColor, true, 0.54);
            r["SystemAccentColorDark1"] = Mix(baseColor, false, 0.18);
            r["SystemAccentColorDark2"] = Mix(baseColor, false, 0.36);
            r["SystemAccentColorDark3"] = Mix(baseColor, false, 0.54);
        }
        catch (Exception ex) { Log("ApplyAccent failed: " + ex.Message); }
    }

    private static Windows.UI.Color ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        return Windows.UI.Color.FromArgb(255,
            Convert.ToByte(hex.Substring(0, 2), 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    // Lighten (toward white) or darken (toward black) by amount [0..1].
    private static Windows.UI.Color Mix(Windows.UI.Color c, bool lighter, double amt)
    {
        byte Ch(byte v) => lighter
            ? (byte)(v + (255 - v) * amt)
            : (byte)(v * (1 - amt));
        return Windows.UI.Color.FromArgb(255, Ch(c.R), Ch(c.G), Ch(c.B));
    }

    /// <summary>Append a line to a startup log next to the exe (and %TEMP% as backup).</summary>
    internal static void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
        foreach (var path in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "startup.log"),
                     Path.Combine(Path.GetTempPath(), "MyNotebook_startup.log"),
                 })
        {
            try { File.AppendAllText(path, line); } catch { /* ignore */ }
        }
    }
}
