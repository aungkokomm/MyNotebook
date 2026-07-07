using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MyNotebook.App;

/// <summary>
/// Tiny localization catalog. Strings live in plain JSON files next to the exe
/// (Strings\en.json, Strings\pt.json, …) so they are easy to translate and add to.
/// English is always loaded as the fallback; any missing key falls back to English,
/// then to the key itself, so the app is never blank if a translation is incomplete.
/// </summary>
public static class Loc
{
    /// <summary>Languages offered in Settings (code + native display name).</summary>
    public static readonly (string Code, string Name)[] Supported =
    {
        ("en", "English"),
        ("pt", "Português"),
    };

    private static Dictionary<string, string> _cur = new();
    private static Dictionary<string, string> _en = new();

    public static string Lang { get; private set; } = "en";

    /// <summary>Resolve and load the active language. Pass the saved preference ("" = follow Windows).</summary>
    public static void Init(string? preference)
    {
        _en = Load("en");
        Lang = Resolve(preference);
        _cur = Lang == "en" ? _en : Load(Lang);
    }

    private static string Resolve(string? pref)
    {
        if (!string.IsNullOrWhiteSpace(pref) && IsSupported(pref!)) return pref!;
        var ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return IsSupported(ui) ? ui : "en";
    }

    public static bool IsSupported(string code)
    {
        foreach (var s in Supported) if (s.Code == code) return true;
        return false;
    }

    private static Dictionary<string, string> Load(string lang)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Strings", lang + ".json");
            if (System.IO.File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(path))
                       ?? new();
        }
        catch { /* fall through to empty -> English fallback */ }
        return new();
    }

    /// <summary>Look up a key; falls back to English, then to the key text itself.</summary>
    public static string T(string key)
        => _cur.TryGetValue(key, out var v) ? v
         : _en.TryGetValue(key, out var e) ? e
         : key;

    /// <summary>Look up a key and format it with args (e.g. "{0} notes").</summary>
    public static string T(string key, params object[] args)
    {
        try { return string.Format(T(key), args); } catch { return T(key); }
    }
}

/// <summary>
/// Attached property that localizes a control from the <see cref="Loc"/> catalog.
/// Put <c>l:L.Key="settings.theme"</c> on an element in XAML, then call
/// <see cref="Apply"/> on the window's root once it has loaded. One attribute per
/// element, no per-string code-behind.
/// </summary>
public static class L
{
    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached("Key", typeof(string), typeof(L), new PropertyMetadata(null));

    public static void SetKey(DependencyObject o, string v) => o.SetValue(KeyProperty, v);
    public static string? GetKey(DependencyObject o) => (string?)o.GetValue(KeyProperty);

    /// <summary>Walk the visual tree under <paramref name="root"/> and localize every tagged element.</summary>
    public static void Apply(DependencyObject? root)
    {
        if (root is null) return;
        Localize(root);
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) Apply(VisualTreeHelper.GetChild(root, i));

        // ComboBox items live in .Items (not the visual tree until the dropdown opens),
        // so localize them directly.
        if (root is ComboBox cb)
            foreach (var item in cb.Items)
                if (item is ComboBoxItem ci && GetKey(ci) is { Length: > 0 } k) ci.Content = Loc.T(k);
    }

    private static void Localize(DependencyObject o)
    {
        var key = GetKey(o);
        if (string.IsNullOrEmpty(key)) return;
        var s = Loc.T(key);
        switch (o)
        {
            case TextBlock tb: tb.Text = s; break;
            case ToggleSwitch ts: ts.Header = s; break;
            case ComboBoxItem ci: ci.Content = s; break;
            case AutoSuggestBox asb: asb.PlaceholderText = s; break;
            case TextBox txt: txt.PlaceholderText = s; break;
            case ButtonBase btn: btn.Content = s; break;   // Button, HyperlinkButton, etc.
            case ContentControl cc: cc.Content = s; break;
        }
    }
}
