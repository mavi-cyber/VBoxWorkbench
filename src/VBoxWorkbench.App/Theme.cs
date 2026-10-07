using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VBoxWorkbench.App;

internal enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Light and dark palettes plus "follow Windows". Standard controls (text boxes, combo boxes, menus, scroll
/// bars, the title bar) are themed by WPF's own Fluent theme; the bench, board and chips use these palettes.
/// </summary>
internal static class Theme
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        // Bench (app chrome)
        ["Bench"] = ("#EFE9DD", "#17191C"),
        ["Panel"] = ("#FBF8F2", "#202328"),
        ["Line"] = ("#D8CFBF", "#383D44"),
        ["Ink"] = ("#2A2622", "#ECE8E1"),
        ["Muted"] = ("#7A7166", "#A39C90"),
        ["Copper"] = ("#B5541C", "#E88A4A"),
        ["OnCopper"] = ("#FFFFFF", "#1E1206"),
        ["CopperSoft"] = ("#F3DCCB", "#3D2B1F"),
        ["Danger"] = ("#B3261E", "#FF8E80"),
        ["Ok"] = ("#2E7D32", "#3F9A5A"),
        // Circuit board (the opened machine)
        ["Pcb"] = ("#0F3B2E", "#0D3327"),
        ["PcbRaised"] = ("#17513F", "#164A3A"),
        ["Trace"] = ("#2E7D62", "#2E7D62"),
        ["Gold"] = ("#E2B755", "#E2B755"),
        ["PcbText"] = ("#E8F3EC", "#E8F3EC"),
        ["PcbMuted"] = ("#9CC4B3", "#9CC4B3"),
        // Command strip
        ["Term"] = ("#1B1D1F", "#0F1011"),
        ["TermText"] = ("#D6E2D0", "#D6E2D0"),
        ["TermMuted"] = ("#8C958A", "#8C958A"),
    };

    // Brushes handed to code-built views. They are never frozen, so changing their colour repaints
    // everything that already uses them. (Brushes stored in Application.Resources get frozen by WPF.)
    private static readonly Dictionary<string, SolidColorBrush> Live = [];

    public static ThemeChoice Choice { get; private set; } = ThemeChoice.System;
    public static bool IsDark { get; private set; }

    private static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VBoxWorkbench", "settings.json");

    private sealed record Settings(string Theme);

    public static SolidColorBrush Brush(string key)
    {
        if (Live.TryGetValue(key, out var b)) return b;
        return Live[key] = new SolidColorBrush(Palette.TryGetValue(key, out var p) ? Parse(IsDark ? p.Dark : p.Light) : Colors.Magenta);
    }

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static void Start()
    {
        try
        {
            // A settings file that was edited by hand or cut short must never stop the app from starting.
            if (File.Exists(SettingsFile) &&
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) is { Theme: not null } s &&
                Enum.TryParse<ThemeChoice>(s.Theme, true, out var saved) && Enum.IsDefined(saved))
                Choice = saved;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException) { }

        Apply();
        // Follow Windows live when the choice is "System".
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && Choice == ThemeChoice.System)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void Set(ThemeChoice choice)
    {
        Choice = choice;
        Apply();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings(choice.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool WindowsPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static void Apply()
    {
        IsDark = Choice == ThemeChoice.Dark || (Choice == ThemeChoice.System && WindowsPrefersDark());
        var app = Application.Current;
        foreach (var (key, (light, dark)) in Palette)
        {
            var color = Parse(IsDark ? dark : light);
            Brush(key).Color = color;
            // XAML refers to these with DynamicResource, which needs a new object to notice the change.
            app.Resources[key] = new SolidColorBrush(color);
        }
#pragma warning disable WPF0001 // ThemeMode is the supported way to get themed standard controls; the API is still marked experimental.
        try
        {
            app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // The palettes above are already applied; only the standard controls keep their previous look.
            App.Log(ex);
        }
#pragma warning restore WPF0001
    }
}
