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

    public AboutWindow()
    {
        InitializeComponent();

        Title = "About — My Notebook";
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var aw = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
        aw.Resize(new Windows.Graphics.SizeInt32(520, 470));
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon))
        {
            aw.SetIcon(icon);
            try { AppLogo.Source = new BitmapImage(new Uri(icon)); } catch { /* logo is optional */ }
        }

        VersionText.Text = $"Version {GetAppVersion()}";
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
