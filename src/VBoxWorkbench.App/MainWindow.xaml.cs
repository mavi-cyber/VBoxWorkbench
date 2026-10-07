using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using VBoxWorkbench.Core;

namespace VBoxWorkbench.App;

public partial class MainWindow : Window
{
    private VBoxRunner? _runner;
    private Catalog _catalog = new();
    private SearchIndex? _index;
    private List<VmRef> _vms = [];
    private VmRef? _currentVm;
    private VmInfo? _info;
    private readonly List<BuiltCommand> _queue = [];
    private readonly Stack<Action> _back = new();
    private Action _showCurrent = () => { };
    private bool _busy;
    private CancellationTokenSource? _runCts;

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VBoxWorkbench");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await StartAsync();
        InputBindings.Add(new KeyBinding(new Relay(() => { SearchBox.Focus(); SearchBox.SelectAll(); }), Key.K, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(async () => await RefreshAsync()), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Relay(GoBack), Key.Left, ModifierKeys.Alt));
    }

    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    // ---------- startup ----------

    private async Task StartAsync()
    {
        string? exe = VBoxLocator.Find();
        if (exe == null)
        {
            StatusText.Text = "VirtualBox wasn't found on this computer.\nInstall it, or point to VBoxManage yourself.";
            var pick = new OpenFileDialog { Title = "Find VBoxManage", Filter = "VBoxManage|VBoxManage*|All files|*.*" };
            if (pick.ShowDialog() != true) return;
            exe = pick.FileName;
        }
        _runner = new VBoxRunner(exe);
        StatusText.Text = "Reading what your VirtualBox can do...";
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            _catalog = await Catalog.LoadAsync(_runner, DataDir, progress);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            StatusText.Text = "Couldn't read the command list from VBoxManage.\n" + ex.Message;
            return;
        }
        _index = new SearchIndex(_catalog);
        int options = _catalog.Commands.Sum(c => c.Synopses.Sum(s => s.Options.Count()));
        VersionText.Text = $"VirtualBox {_catalog.Version}";
        VersionText.ToolTip = $"{_catalog.Commands.Count} commands, {_catalog.Commands.Sum(c => c.Synopses.Count)} actions and {options} options, read from {exe}";
        StatusText.Text = "";
        await RefreshAsync();
        if (_vms.Count > 0) await ShowWorkbenchAsync(_vms[0]);
        else ShowRoom(Room.Machines);
        ApplyStartupArguments();
    }

    /// <summary>Deep links: --show "snapshot take", --room Media, --search "forward port".</summary>
    private void ApplyStartupArguments()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Contains("--about")) Dispatcher.BeginInvoke(() => new AboutWindow(this, _catalog.Version).Show());
        for (int i = 1; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--show": OpenForm(args[i + 1]); break;
                case "--room" when Enum.TryParse<Room>(args[i + 1], true, out var room): ShowRoom(room); break;
                case "--search": SearchBox.Text = args[i + 1]; SearchBox.Focus(); break;
                case "--theme" when Enum.TryParse<ThemeChoice>(args[i + 1], true, out var theme): Theme.Set(theme); break;
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (_runner == null) return;
        var all = await _runner.RunAsync(["list", "vms"]);
        var running = VmInfo.ParseVmList((await _runner.RunAsync(["list", "runningvms"])).Output).Select(v => v.Uuid).ToHashSet();
        _vms = VmInfo.ParseVmList(all.Output).Select(v => v with { Running = running.Contains(v.Uuid) }).ToList();
        BuildNav();
        if (_currentVm != null)
        {
            var again = _vms.FirstOrDefault(v => v.Uuid == _currentVm.Uuid);
            if (again == null) { _currentVm = null; _info = null; ShowRoom(Room.Machines); }
            else if (ContentHost.Content is FrameworkElement { Tag: "workbench" }) await ShowWorkbenchAsync(again);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    // ---------- navigation ----------

    private void BuildNav()
    {
        NavPanel.Children.Clear();
        NavPanel.Children.Add(Ui.Text("Machines", 11.5, "Muted").Margin(10, 4, 0, 4));
        foreach (var vm in _vms)
        {
            var b = Ui.Button(vm.Name, async () => await ShowWorkbenchAsync(vm), "NavButton", Ui.IcoPc, vm.Running ? "Running" : null);
            if (vm.Running) b.Foreground = Ui.Brush("Ok");
            if (_currentVm?.Uuid == vm.Uuid) b.Background = Ui.Brush("CopperSoft");
            NavPanel.Children.Add(b);
        }
        NavPanel.Children.Add(Ui.Button("New, import, add", () => ShowRoom(Room.Machines), "NavButton", Ui.IcoAdd));
        NavPanel.Children.Add(Ui.Text("Rooms", 11.5, "Muted").Margin(10, 12, 0, 4));
        (Room, string)[] rooms =
        [
            (Room.Media, Ui.IcoDisk), (Room.Network, Ui.IcoNet), (Room.Guest, Ui.IcoGuest), (Room.Diagnostics, Ui.IcoDiag),
            (Room.Cloud, Ui.IcoCloud), (Room.Host, Ui.IcoSettings), (Room.More, Ui.IcoMore),
        ];
        foreach (var (room, icon) in rooms)
        {
            if (room == Room.More && !_catalog.Commands.Any(c => Curation.RoomOf(c.Name) == Room.More)) continue;
            NavPanel.Children.Add(Ui.Button(Curation.RoomTitle(room), () => ShowRoom(room), "NavButton", icon));
        }
    }

    private void SetContent(FrameworkElement view, Action show, bool shelf = false)
    {
        _showCurrent = show;
        ContentHost.Content = view;
        ShelfBorder.Visibility = shelf ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = "";
    }

    private void Push() => _back.Push(_showCurrent);

    private void GoBack()
    {
        if (_back.Count > 0) _back.Pop()();
    }

    private FrameworkElement WithBack(FrameworkElement view)
    {
        var dock = new DockPanel();
        var back = Ui.Button("Back", GoBack, glyph: Ui.IcoBack, tip: "Alt+Left").Margin(0, 0, 0, 10);
        back.HorizontalAlignment = HorizontalAlignment.Left;
        DockPanel.SetDock(back, Dock.Top);
        dock.Children.Add(back);
        dock.Children.Add(view);
        return dock;
    }

    // ---------- forms ----------

    internal void OpenForm(CommandDoc doc, Synopsis synopsis, string? title = null, string? focus = null,
        Dictionary<string, string>? presets = null, List<string>? positionals = null, string? index = null, string? vm = null, string? warning = null)
    {
        if (_index == null) return;
        presets = OptionNames.Remap(synopsis, presets);
        if (focus != null) focus = OptionNames.Find(synopsis, focus)?.Name ?? focus;
        if (warning == null && doc.Name == "modifyvm" && _info is { CanModify: false } && (vm ?? _currentVm?.Name) == _info.Name)
            warning = $"{_info.Name} is {_info.State}. These settings only apply to a powered-off machine, so run this after shutting it down.";
        var ctx = new FormContext
        {
            Doc = doc, Synopsis = synopsis, Title = title ?? SearchIndex.TitleOf(doc, synopsis),
            VmNames = _vms.Select(v => v.Name).ToList(), Vm = vm ?? _currentVm?.Name,
            FocusOption = focus, Presets = presets, Positionals = positionals, Index = index, Warning = warning,
        };
        var form = new CommandForm(ctx);
        form.Queue += c => { Enqueue(c); GoBack(); };
        form.RunNow += async c => { GoBack(); await RunAsync([c]); };
        form.ShowManual += () => ShowManual(doc);
        Push();
        void Show() => SetContent(WithBack(form), Show);
        Show();
    }

    internal void OpenForm(string path, string? title = null, string? focus = null, Dictionary<string, string>? presets = null,
        List<string>? positionals = null, string? index = null, Func<Synopsis, bool>? filter = null)
    {
        foreach (var doc in _catalog.Commands)
        {
            var syn = doc.Synopses.FirstOrDefault(s => s.Path == path && (filter?.Invoke(s) ?? true));
            if (syn == null) continue;
            OpenForm(doc, syn, title, focus, presets, positionals, index);
            return;
        }
        AppendOutput($"Your VirtualBox version has no \"{path}\" command.");
    }

    internal void ShowManual(CommandDoc doc)
    {
        var box = new TextBox
        {
            Text = doc.FullText, IsReadOnly = true, FontFamily = Ui.Mono, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Ui.Brush("Panel"), VerticalContentAlignment = VerticalAlignment.Top, Padding = new Thickness(12),
        };
        Push();
        void Show() => SetContent(WithBack(box), Show);
        Show();
    }

    // ---------- search ----------

    private void SearchBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => UpdateSearch();

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearch();
    }

    private void UpdateSearch()
    {
        if (_index == null) return;
        SearchList.Items.Clear();
        foreach (var hit in _index.Search(SearchBox.Text, 14))
        {
            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };
            var where = Ui.Text(Curation.RoomTitle(Curation.RoomOf(hit.Doc.Name)), 11.5, "Muted").Margin(10, 0, 0, 0);
            DockPanel.SetDock(where, Dock.Right);
            row.Children.Add(where);
            var text = new StackPanel();
            text.Children.Add(Ui.Text(hit.Title, 13.5, bold: hit.Curated));
            text.Children.Add(Ui.Text(hit.Subtitle, 11.5, "Copper", mono: true));
            row.Children.Add(text);
            SearchList.Items.Add(new ListBoxItem { Content = row, Tag = hit, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (SearchList.Items.Count == 0)
            SearchList.Items.Add(new ListBoxItem { Content = Ui.Text("Nothing matches. Try another word, or a command name.", 13, "Muted"), IsEnabled = false });
        SearchList.SelectedIndex = 0;
        SearchPopup.IsOpen = SearchBox.IsKeyboardFocusWithin;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { SearchList.SelectedIndex = Math.Min(SearchList.SelectedIndex + 1, SearchList.Items.Count - 1); e.Handled = true; }
        else if (e.Key == Key.Up) { SearchList.SelectedIndex = Math.Max(SearchList.SelectedIndex - 1, 0); e.Handled = true; }
        else if (e.Key == Key.Enter) { PickSearch(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchPopup.IsOpen = false; e.Handled = true; }
        (SearchList.SelectedItem as ListBoxItem)?.BringIntoView();
    }

    private void SearchList_Click(object sender, MouseButtonEventArgs e) => PickSearch();

    private void PickSearch()
    {
        if ((SearchList.SelectedItem as ListBoxItem)?.Tag is not SearchEntry hit) return;
        SearchPopup.IsOpen = false;
        SearchBox.Text = "";
        OpenForm(hit.Doc, hit.Synopsis, hit.Curated ? hit.Title : null, hit.FocusOption, hit.Presets ?? (hit.FocusOption != null ? new() { [hit.FocusOption] = "" } : null));
    }

    // ---------- queue ----------

    internal void Enqueue(BuiltCommand c)
    {
        _queue.Add(c);
        RenderQueue();
    }

    private void RenderQueue()
    {
        QueuePanel.Children.Clear();
        QueueTitle.Text = _queue.Count == 0 ? "Queue is empty. Pick something on the bench or search above."
            : _queue.Count == 1 ? "Queue · 1 command" : $"Queue · {_queue.Count} commands";
        foreach (var c in _queue.ToList())
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var remove = Ui.Button("", () => { _queue.Remove(c); RenderQueue(); }, "TermButton", Ui.IcoClose, "Remove from queue");
            remove.Padding = new Thickness(6, 2, 2, 2);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            if (c.Dangerous) row.Children.Add(Ui.Icon(Ui.IcoWarn, 13, "Gold").With(i => i.ToolTip = Curation.DangerNote(c.Args)));
            var text = Ui.Text(c.Display, 12.5, "TermText", mono: true);
            text.ToolTip = c.Title;
            row.Children.Add(text);
            QueuePanel.Children.Add(row);
        }
    }

    private void QueueClear_Click(object sender, RoutedEventArgs e) { _queue.Clear(); RenderQueue(); }

    private void QueueCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_queue.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, _queue.Select(c => c.Display)));
    }

    private void QueueSave_Click(object sender, RoutedEventArgs e)
    {
        if (_queue.Count == 0) return;
        var d = new SaveFileDialog { Filter = "Windows batch file|*.cmd|PowerShell script|*.ps1|Shell script|*.sh", FileName = "vbox-steps" };
        if (d.ShowDialog() != true) return;
        string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
        var sb = new StringBuilder();
        if (ext == ".sh") sb.Append("#!/bin/sh\nset -e\n");
        else if (ext == ".cmd") sb.Append("@echo off\r\n");
        string nl = ext == ".sh" ? "\n" : "\r\n";
        foreach (var c in _queue)
        {
            string line = c.Display;
            if (ext == ".cmd") line = line.Replace("%", "%%") + " || exit /b 1";
            else if (ext == ".ps1") line = "& " + line + "; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }";
            sb.Append(line).Append(nl);
        }
        try
        {
            File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(false));
            AppendOutput("Script saved to " + d.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendOutput("Couldn't save the script: " + ex.Message);
        }
    }

    private async void QueueRun_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) { _runCts?.Cancel(); return; }
        var batch = _queue.ToList();
        if (batch.Count == 0) return;
        bool finished = await RunAsync(batch, fromQueue: true);
        if (finished) { _queue.Clear(); RenderQueue(); }
    }

    /// <summary>Runs commands in order and stops at the first failure. Returns true when all succeeded.</summary>
    internal async Task<bool> RunAsync(List<BuiltCommand> batch, bool fromQueue = false)
    {
        if (_runner == null || _busy) return false;
        var risky = batch.Where(c => c.Dangerous).ToList();
        if (risky.Count > 0)
        {
            string msg = string.Join("\n\n", risky.Select(c => c.Display + "\n" + Curation.DangerNote(c.Args)));
            if (MessageBox.Show(this, msg + "\n\nRun it?", "This can't be undone", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK)
                return false;
        }
        _busy = true;
        _runCts = new CancellationTokenSource();
        RunButton.Content = "Stop";
        ShowOutput(true);
        bool ok = true;
        try
        {
            foreach (var c in batch)
            {
                AppendOutput("> " + c.Display);
                if (!c.Runnable)
                {
                    AppendOutput("Skipped: this tool isn't available on this host. Copy the command to a Linux or macOS host.");
                    continue;
                }
                var result = await _runner.RunAsync(c.Args, _runCts.Token, line => Dispatcher.BeginInvoke(() => AppendOutput(line)), useGlobalArgs: true, program: c.Program);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                if (fromQueue && result.Ok) { _queue.Remove(c); RenderQueue(); }
                if (!result.Ok)
                {
                    AppendOutput(result.ExitCode == -2 ? "Stopped." : $"Failed with exit code {result.ExitCode}. The rest of the queue was not run.");
                    ok = false;
                    break;
                }
                AppendOutput("Done.");
            }
        }
        finally
        {
            _busy = false;
            RunButton.Content = "Run queue";
            _runCts.Dispose();
            _runCts = null;
        }
        await RefreshAsync();
        return ok;
    }

    /// <summary>Runs a read-only command and shows what it printed.</summary>
    internal async Task LookAsync(params string[] args)
    {
        if (_runner == null) return;
        ShowOutput(true);
        AppendOutput("> VBoxManage " + string.Join(' ', args.Select(CommandLine.Quote)));
        var r = await _runner.RunAsync(args);
        AppendOutput(r.Combined.Length > 0 ? r.Combined : "(nothing to show)");
    }

    private void AppendOutput(string line)
    {
        OutputBox.AppendText(line + Environment.NewLine);
        if (OutputBox.Text.Length > 400_000) OutputBox.Text = OutputBox.Text[^300_000..];
        OutputBox.ScrollToEnd();
    }

    private void ShowOutput(bool show)
    {
        OutputBox.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutputToggle.Content = show ? "Hide output" : "Show output";
    }

    private void OutputToggle_Click(object sender, RoutedEventArgs e) => ShowOutput(OutputBox.Visibility != Visibility.Visible);

    // ---------- about ----------

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow(this, _catalog.Version).ShowDialog();

    // ---------- theme ----------

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ThemeButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        (ThemeChoice, string)[] items = [(ThemeChoice.System, "Follow Windows"), (ThemeChoice.Light, "Light"), (ThemeChoice.Dark, "Dark")];
        foreach (var (choice, label) in items)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Theme.Choice == choice };
            item.Click += (_, _) => Theme.Set(choice);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- run options (manual 8.3 and 8.4) ----------

    private void RunOptions_Click(object sender, RoutedEventArgs e)
    {
        if (_runner == null) return;
        string Current(string prefix) => _runner.GlobalArgs.FirstOrDefault(a => a.StartsWith(prefix))?[prefix.Length..] ?? "";
        var pwFile = new TextBox { Text = Current("--settingspwfile="), Width = 360 };
        var response = new TextBox { Text = Current("@"), Width = 360 };
        var panel = Ui.Column(
            Ui.Text("These are added in front of every command you run from the queue.", 12.5, "Muted", wrap: true).Margin(0, 0, 0, 12),
            Ui.Text("Settings password file  (--settingspwfile)", 13, bold: true),
            Ui.Text("Encrypts stored secrets such as the iSCSI password. Once you use it, you must keep using it.", 12, "Muted", wrap: true).Margin(0, 2, 0, 4),
            pwFile,
            Ui.Text("Response file  (@file)", 13, bold: true).Margin(0, 14, 0, 0),
            Ui.Text("Loads extra arguments from a file.", 12, "Muted").Margin(0, 2, 0, 4),
            response,
            Ui.Text("--nologo is always on so output stays clean. Version: " + _catalog.Version, 12, "Muted", wrap: true).Margin(0, 14, 0, 12));
        var dialog = new Window
        {
            Title = "Run options", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Brush("Bench"), Content = panel.Margin(18, 16, 18, 12),
        };
        panel.Children.Add(Ui.Row(
            Ui.Button("Save", () =>
            {
                _runner.GlobalArgs.Clear();
                if (pwFile.Text.Trim().Length > 0) _runner.GlobalArgs.Add("--settingspwfile=" + pwFile.Text.Trim());
                if (response.Text.Trim().Length > 0) _runner.GlobalArgs.Add("@" + response.Text.Trim());
                dialog.Close();
            }, "PrimaryButton"),
            Ui.Button("Cancel", dialog.Close),
            Ui.Button("Rebuild command list", async () =>
            {
                dialog.Close();
                try { foreach (var f in Directory.GetFiles(DataDir, "catalog-*.json")) File.Delete(f); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                await StartAsync();
            }, tip: "Read the commands from VBoxManage again, for example after updating VirtualBox")));
        dialog.ShowDialog();
    }
}
