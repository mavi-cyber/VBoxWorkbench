using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace VBoxWorkbench.App;

/// <summary>The About box: logo, version, project link, copyright and licence.</summary>
internal sealed class AboutWindow : Window
{
    public const string Website = "https://github.com/mavi-cyber/VBoxWorkbench";

    /// <summary>Version and, when the build knows it, the short commit id: ("0.1.1", "fc7e283").</summary>
    public static (string Version, string Commit) BuildInfo()
    {
        string info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = info.IndexOf('+');
        string version = plus < 0 ? info : info[..plus];
        string commit = plus < 0 ? "" : info[(plus + 1)..];
        return (version.Length > 0 ? version : "development build", commit.Length > 7 ? commit[..7] : commit);
    }

    public AboutWindow(Window owner, string virtualBoxVersion)
    {
        Owner = owner;
        Title = "About VBox Workbench";
        Icon = owner.Icon;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Bench");
        SetResourceReference(ForegroundProperty, "Ink");
        FontFamily = owner.FontFamily;

        var (version, commit) = BuildInfo();
        var panel = new StackPanel { Margin = new Thickness(28, 26, 28, 20) };

        void Centered(FrameworkElement element, double top = 0)
        {
            element.HorizontalAlignment = HorizontalAlignment.Center;
            element.Margin = new Thickness(0, top, 0, 0);
            if (element is TextBlock t) t.TextAlignment = TextAlignment.Center;
            panel.Children.Add(element);
        }

        Centered(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")), Width = 112, Height = 112,
        });
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(panel.Children[0], System.Windows.Media.BitmapScalingMode.HighQuality);

        Centered(Ui.Text("VBox Workbench", 20, bold: true), 14);
        Centered(Ui.Text(version + (commit.Length > 0 ? $"  ({commit})" : ""), 13.5), 8);
        Centered(Ui.Text("A graphical front end for VBoxManage", 13.5), 8);

        var link = new Hyperlink(new Run("Visit the VBox Workbench website")) { NavigateUri = new Uri(Website), ToolTip = Website };
        link.SetResourceReference(TextElement.ForegroundProperty, "Copper");
        link.RequestNavigate += (_, e) => { Open(e.Uri.AbsoluteUri); e.Handled = true; };
        Centered(new TextBlock(link) { FontSize = 13.5 }, 10);

        Centered(Ui.Text("Copyright © 2026 mavi-cyber", 12.5), 16);
        Centered(Ui.Text("Free software under the GNU General Public License, version 3. It comes with no warranty.", 12, "Muted", wrap: true), 4);
        if (virtualBoxVersion.Length > 0)
            Centered(Ui.Text("Working with VirtualBox " + virtualBoxVersion, 12, "Muted"), 10);
        Centered(Ui.Text("An independent project, not affiliated with Oracle. VirtualBox is a trademark of Oracle.", 12, "Muted", wrap: true), 4);

        var buttons = Ui.Row(
            Ui.Button("License", () => Open(Website + "/blob/main/LICENSE"), tip: "Read the GPL-3.0 text"),
            Ui.Button("Report a problem", () => Open(Website + "/issues")),
            Ui.Button("Close", Close, "PrimaryButton"));
        Centered(buttons, 18);

        Content = panel;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(url, "Couldn't open the browser", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
