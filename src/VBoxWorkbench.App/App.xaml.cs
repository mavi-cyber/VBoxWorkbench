using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace VBoxWorkbench.App;

public partial class App : Application
{
    private static string LogFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VBoxWorkbench", "error.log");

    private bool _reporting;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Any error that reaches this point would otherwise close the app without a word.
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log(args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception ex) Log(ex); };
        Theme.Start();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true;
        if (_reporting) return; // one dialog at a time, even if errors come in a burst
        _reporting = true;
        try
        {
            MessageBox.Show(
                "Something went wrong, but the app is still running.\n\n" + e.Exception.Message +
                "\n\nDetails were saved to:\n" + LogFile + "\n\nIf this keeps happening, please report it from the About box.",
                "VBox Workbench", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>Appends an error to the log file. Never throws.</summary>
    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1_000_000) File.Delete(LogFile);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {AboutWindow.BuildInfo().Version}\n{ex}\n\n");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
