using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;

namespace MyNotebook.App;

/// <summary>
/// Standalone About window: app name + version, the source repository link, and a
/// clear notice that updates are manual (the app never updates itself).
/// </summary>
public sealed partial class AboutWindow : Window
{
    private const string RepoUrl = "https://github.com/aungkokomm/MyNotebook";
    private readonly Microsoft.UI.Windowing.AppWindow _aw;

    public AboutWindow()
    {
        InitializeComponent();

        Title = "About — My Notebook";
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        if (_aw.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.IsResizable = false;
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon))
        {
            _aw.SetIcon(icon);
            try { AppLogo.Source = new BitmapImage(new Uri(icon)); } catch { /* logo is optional */ }
        }

        VersionText.Text = $"Version {GetAppVersion()}";

        // Size the window to exactly fit its content (no clipping, no scrollbar).
        RootPanel.Loaded += (_, _) => FitToContent();
        RootPanel.SizeChanged += (_, _) => FitToContent();
    }

    private void FitToContent()
    {
        try
        {
            var scale = RootPanel.XamlRoot?.RasterizationScale ?? 1.0;
            RootPanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = RootPanel.DesiredSize;                 // width capped by MaxWidth; height wraps at that width
            double pad = 28 * 2;                            // ScrollViewer padding
            double widthLogical = d.Width + pad + 4;
            double heightLogical = d.Height + pad + 40;     // + title bar / borders
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                _aw.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            double maxH = area.WorkArea.Height / scale - 48;
            heightLogical = Math.Clamp(heightLogical, 200, maxH);
            _aw.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(widthLogical * scale),
                (int)Math.Round(heightLogical * scale)));
        }
        catch { }
    }

    private void CheckUpdates_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/releases/latest");

    private void Issues_Click(object sender, RoutedEventArgs e) => OpenUrl(RepoUrl + "/issues");

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private static string GetAppVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
