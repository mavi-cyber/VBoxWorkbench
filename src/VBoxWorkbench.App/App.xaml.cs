using System.Windows;

namespace VBoxWorkbench.App;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e) => Theme.Start();
}
