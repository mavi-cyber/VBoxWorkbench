using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace VBoxWorkbench.App;

/// <summary>Small builders so the views read as layout, not as property soup.</summary>
internal static class Ui
{
    public static Brush Brush(string key) => Theme.Brush(key);
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static FontFamily Mono => (FontFamily)Application.Current.Resources["Mono"];
    public static FontFamily Icons => (FontFamily)Application.Current.Resources["Icons"];

    // Segoe Fluent Icons glyphs
    public const string IcoPlay = "", IcoPause = "", IcoStop = "", IcoPower = "", IcoSave = "",
        IcoRefresh = "", IcoAdd = "", IcoSearch = "", IcoPc = "", IcoDisk = "", IcoNet = "",
        IcoGuest = "", IcoDiag = "", IcoCloud = "", IcoSettings = "", IcoMore = "", IcoBack = "",
        IcoCopy = "", IcoLock = "", IcoWarn = "", IcoDelete = "", IcoBook = "", IcoChip = "",
        IcoPlug = "", IcoScreen = "", IcoShare = "", IcoShield = "", IcoBranch = "", IcoUsb = "",
        IcoDisc = "", IcoFolder = "", IcoCamera = "", IcoClose = "", IcoBox = "";

    public static TextBlock Text(string text, double size = 13, string brush = "Ink", bool bold = false, bool wrap = false, bool mono = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, Foreground = Brush(brush),
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (wrap) t.TextTrimming = TextTrimming.None;
        if (mono) t.FontFamily = Mono;
        return t;
    }

    public static TextBlock Icon(string glyph, double size = 14, string brush = "Ink") => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, Foreground = Brush(brush),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0),
    };

    /// <summary>Button content with an icon in front of the label. The icon follows the button's foreground.</summary>
    public static object Labelled(string glyph, string label)
    {
        var t = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
        });
        if (glyph.Length > 0) t.Inlines.Add(new Run(glyph + "  ") { FontFamily = Icons });
        t.Inlines.Add(new Run(label));
        return t;
    }

    public static Button Button(string label, Action onClick, string style = "FlatButton", string glyph = "", string? tip = null)
    {
        var b = new Button { Content = Labelled(glyph, label), Style = Style(style) };
        if (tip != null) b.ToolTip = tip;
        b.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        return b;
    }

    /// <summary>Copies text. Returns false when another program holds the clipboard, which Windows reports as an error.</summary>
    public static bool Copy(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    /// <summary>A tooltip that wraps, for the long option descriptions.</summary>
    public static ToolTip Tip(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
    };

    public static Border Card(UIElement child, string background = "Panel", string border = "Line", double pad = 14) => new()
    {
        Child = child, Background = Brush(background), BorderBrush = Brush(border), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = new Thickness(pad), Margin = new Thickness(0, 0, 12, 12),
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var p = new StackPanel();
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static T With<T>(this T element, Action<T> configure) where T : UIElement
    {
        configure(element);
        return element;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }
}
