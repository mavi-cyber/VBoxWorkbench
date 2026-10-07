using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using VBoxWorkbench.Core;

namespace VBoxWorkbench.App;

/// <summary>What a form needs to know about the world around it.</summary>
internal sealed class FormContext
{
    public required CommandDoc Doc { get; init; }
    public required Synopsis Synopsis { get; init; }
    public string Title { get; init; } = "";
    public IReadOnlyList<string> VmNames { get; init; } = [];
    public string? Vm { get; init; }
    public string? FocusOption { get; init; }
    public Dictionary<string, string>? Presets { get; init; }
    /// <summary>Values for positional elements, in order of appearance (after the VM).</summary>
    public List<string>? Positionals { get; init; }
    public string? Index { get; init; }
    public string? Warning { get; init; }
}

/// <summary>
/// A form generated from one usage line. Every element of the synopsis gets a control, so the form is
/// always as complete as the installed VBoxManage, whatever its version.
/// </summary>
internal sealed class CommandForm : UserControl
{
    private sealed class RowState
    {
        public required SynopsisElement Element;
        public CheckBox? Enabled;
        public Func<string> Value = () => "";
        public Func<string> Index = () => "";
        public FrameworkElement Row = null!;
        public bool Pinned;
    }

    private readonly FormContext _ctx;
    private readonly List<RowState> _rows = [];
    private readonly TextBlock _preview = Ui.Text("", 13, "TermText", mono: true, wrap: true);
    private readonly TextBlock _error = Ui.Text("", 12.5, "Danger", wrap: true);
    private readonly StackPanel _optionHost = new();
    private readonly TextBox _filter = new() { Width = 220 };
    private readonly HashSet<string> _claimed = [];
    private bool _showAll;
    private Button? _toggle;

    public event Action<BuiltCommand>? Queue;
    public event Action<BuiltCommand>? RunNow;
    public event Action? ShowManual;

    public CommandForm(FormContext ctx)
    {
        _ctx = ctx;
        var root = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };

        root.Children.Add(Ui.Text(ctx.Title, 20, bold: true, wrap: true));
        root.Children.Add(Ui.Text(ctx.Synopsis.Path + (ctx.Doc.Summary.Length > 0 ? "  ·  " + ctx.Doc.Summary : ""), 12.5, "Muted", wrap: true).Margin(0, 2, 0, 10));

        if (!ctx.Doc.Runnable)
            root.Children.Add(Banner(Ui.IcoWarn, "This tool only exists on Linux and macOS hosts. You can build and copy the command here, but it can't run on this computer."));
        if (ctx.Warning != null) root.Children.Add(Banner(Ui.IcoLock, ctx.Warning));
        if (ctx.Doc.Legacy)
            root.Children.Add(Ui.Text("Your VirtualBox version prints no descriptions. The explanations below are borrowed from a newer manual; the syntax is your version's own.", 12, "Muted", wrap: true).Margin(0, 0, 0, 10));

        var required = new StackPanel();
        int positional = 0;
        foreach (var e in ctx.Synopsis.Elements)
        {
            var row = BuildRow(e, ref positional);
            _rows.Add(row);
            bool isOption = e.Kind == ElementKind.Option && !e.Required;
            (isOption ? _optionHost : required).Children.Add(row.Row);
        }
        root.Children.Add(required);

        int optionCount = _optionHost.Children.Count;
        if (optionCount > 0)
        {
            var head = new DockPanel { Margin = new Thickness(0, 12, 0, 6) };
            head.Children.Add(Ui.Text("Options", 14, bold: true));
            bool focused = _rows.Any(r => r.Pinned) && optionCount > 6;
            _showAll = !focused;
            if (focused)
            {
                _toggle = Ui.Button($"Show all {optionCount} options", ToggleAll).Margin(12, 0, 0, 0);
                head.Children.Add(_toggle);
            }
            if (optionCount > 10)
            {
                _filter.Margin = new Thickness(12, 0, 0, 0);
                _filter.ToolTip = "Filter options by name or description";
                _filter.TextChanged += (_, _) => { if (_filter.Text.Length > 0) _showAll = true; ApplyFilter(); };
                head.Children.Add(Ui.Row(Ui.Icon(Ui.IcoSearch, 12, "Muted").Margin(12, 0, 4, 0), _filter));
            }
            root.Children.Add(head);
            root.Children.Add(_optionHost);
            ApplyFilter();
        }

        root.Children.Add(_error.Margin(0, 8, 0, 0));
        root.Children.Add(new Border
        {
            Background = Ui.Brush("Term"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 8, 0, 10), Child = _preview,
        });
        var buttons = Ui.Row(
            Ui.Button("Add to queue", () => Emit(Queue), "PrimaryButton", Ui.IcoAdd),
            Ui.Button("Run now", () => Emit(RunNow), glyph: Ui.IcoPlay).With(b => b.IsEnabled = ctx.Doc.Runnable),
            Ui.Button("Copy", () => { if (TryBuild(out var c) && !Ui.Copy(c.Display)) _error.Text = "Couldn't copy: another program is using the clipboard. Try again."; }, glyph: Ui.IcoCopy),
            Ui.Button("Read the manual page", () => ShowManual?.Invoke(), glyph: Ui.IcoBook));
        root.Children.Add(buttons);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) };
        UpdatePreview();
    }

    private static Border Banner(string glyph, string text) => new()
    {
        Background = Ui.Brush("CopperSoft"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 10),
        Child = new DockPanel().With(d =>
        {
            d.Children.Add(Ui.Icon(glyph, 14, "Copper"));
            d.Children.Add(Ui.Text(text, 12.5, wrap: true));
        }),
    };

    private void ToggleAll()
    {
        _showAll = !_showAll;
        if (_toggle != null) _toggle.Content = _showAll ? "Show only what I picked" : $"Show all {_optionHost.Children.Count} options";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string f = _filter.Text.Trim().ToLowerInvariant();
        foreach (var r in _rows)
        {
            if (r.Element.Kind != ElementKind.Option || r.Element.Required) continue;
            bool match = f.Length == 0 || r.Element.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                         || _ctx.Doc.HelpFor(r.Element).Contains(f, StringComparison.OrdinalIgnoreCase);
            bool visible = match && (_showAll || r.Pinned || r.Enabled?.IsChecked == true);
            r.Row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private RowState BuildRow(SynopsisElement e, ref int positional)
    {
        var state = new RowState { Element = e };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        state.Row = grid;

        void Put(UIElement el, int col) { Grid.SetColumn(el, col); grid.Children.Add(el); }

        TextBox? indexBox = null;
        if (e.Indexed)
        {
            indexBox = new TextBox { Width = 34, Text = _ctx.Index ?? "1", ToolTip = "Which one (adapter, port or slot number)", Margin = new Thickness(6, 0, 0, 0) };
            indexBox.TextChanged += (_, _) => UpdatePreview();
            state.Index = () => indexBox.Text.Trim();
        }

        switch (e.Kind)
        {
            case ElementKind.Keyword:
            {
                var label = Ui.Row(Ui.Text(e.Name + (e.Indexed ? " #" : ""), 13, "Muted", mono: true));
                if (indexBox != null) label.Children.Add(indexBox);
                Put(label, 0);
                if (!e.Indexed) grid.Visibility = Visibility.Collapsed;
                break;
            }
            case ElementKind.Passthrough:
            {
                Put(Ui.Text("Arguments for the program", 13), 0);
                var box = new TextBox { ToolTip = "Everything here is passed on after \"--\". Put quotes around an argument that contains spaces." };
                box.TextChanged += (_, _) => UpdatePreview();
                state.Value = () => box.Text;
                state.Enabled = new CheckBox { IsChecked = false, Visibility = Visibility.Collapsed };
                box.TextChanged += (_, _) => state.Enabled.IsChecked = box.Text.Trim().Length > 0;
                Put(box, 1);
                Put(Ui.Text(e.Placeholder, 12, "Muted").Margin(10, 0, 0, 0), 2);
                break;
            }
            case ElementKind.Positional:
            {
                string label = e.IsVm ? "Virtual machine" : Humanize(e.Placeholder.Length > 0 ? e.Placeholder : e.Name);
                Put(Ui.Text(label + (e.Required ? " *" : ""), 13, wrap: true), 0);
                string? preset = e.IsVm ? _ctx.Vm : _ctx.Positionals != null && positional < _ctx.Positionals.Count ? _ctx.Positionals[positional] : null;
                if (!e.IsVm) positional++;
                var (control, getter) = ValueControl(e, e.IsVm ? _ctx.VmNames : e.Choices, preset ?? "", null);
                state.Value = getter;
                Put(control, 1);
                string hint = e.IsVm ? "Name or UUID" : e.Repeatable ? "Several values, separated by spaces" : e.Required ? "" : "Optional";
                Put(Ui.Text(hint, 12, "Muted").Margin(10, 0, 0, 0), 2);
                break;
            }
            case ElementKind.Option:
            {
                // Two rows can share a name (--nat-pfN to add, --nat-pfN=delete=... to remove): only the first takes the preset.
                string? preset = null;
                bool first = _claimed.Add(e.Name);
                bool hasPreset = first && _ctx.Presets?.TryGetValue(e.Name, out preset) == true;
                state.Pinned = first && (hasPreset || e.Name == _ctx.FocusOption);
                var check = new CheckBox
                {
                    IsChecked = e.Required || (hasPreset && (!e.HasValue || !string.IsNullOrEmpty(preset))),
                    IsEnabled = !e.Required, VerticalAlignment = VerticalAlignment.Center,
                    Content = Ui.Text(e.Name + (e.Indexed ? "N" : "") + (e.Required ? " *" : ""), 13, mono: true),
                };
                check.Checked += (_, _) => UpdatePreview();
                check.Unchecked += (_, _) => UpdatePreview();
                state.Enabled = check;
                var left = Ui.Row(check);
                if (indexBox != null) left.Children.Add(indexBox);
                Put(left, 0);

                if (e.HasValue)
                {
                    var (control, getter) = ValueControl(e, e.Choices, preset ?? "", () => { if (!e.Required) check.IsChecked = true; });
                    state.Value = getter;
                    Put(control, 1);
                }
                string help = _ctx.Doc.HelpFor(e);
                var hint = Ui.Text(help.Length > 0 ? SearchIndex.FirstSentence(help) : e.HasValue ? e.Placeholder : "", 12, "Muted", wrap: true).Margin(10, 0, 0, 0);
                if (help.Length > 0) grid.ToolTip = Ui.Tip(help.Length > 900 ? help[..900] + "..." : help);
                Put(hint, e.HasValue ? 2 : 1);
                if (!e.HasValue) Grid.SetColumnSpan(hint, 2);
                break;
            }
        }
        return state;
    }

    private static string Humanize(string placeholder)
    {
        string s = placeholder.Replace('-', ' ').Replace('_', ' ').Trim();
        return s.Length == 0 ? "Value" : char.ToUpper(s[0]) + s[1..];
    }

    private static bool LooksLikeFile(SynopsisElement e)
    {
        string s = (e.Name + " " + e.Placeholder).ToLowerInvariant();
        return s.Contains("file") || s.Contains("iso") || s.Contains("ovfname") || s.Contains("tarball") || s.Contains("medium")
               || s.Contains("--certificate") || s.Contains("private-key") || s.Contains("--disk") || s.Contains("pathname");
    }

    private static bool LooksLikeFolder(SynopsisElement e)
    {
        string s = (e.Name + " " + e.Placeholder).ToLowerInvariant();
        return s.Contains("folder") || s.Contains("hostpath") || s.Contains("directory") || s.Contains("mountpoint");
    }

    private (FrameworkElement Control, Func<string> Getter) ValueControl(SynopsisElement e, IReadOnlyList<string> choices, string preset, Action? onEdit)
    {
        FrameworkElement input;
        Func<string> getter;
        Action<string> setter;
        if (choices.Count > 0)
        {
            // Editable on purpose: usage lines mix fixed choices with free values ("none | device-name").
            var combo = new ComboBox { IsEditable = true, ItemsSource = choices, Text = preset, ToolTip = e.Placeholder };
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { onEdit?.Invoke(); UpdatePreview(); }));
            combo.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(() => { onEdit?.Invoke(); UpdatePreview(); });
            input = combo;
            getter = () => combo.Text.Trim();
            setter = v => combo.Text = v;
        }
        else
        {
            var box = new TextBox { Text = preset, ToolTip = e.Placeholder };
            box.TextChanged += (_, _) => { onEdit?.Invoke(); UpdatePreview(); };
            input = box;
            getter = () => box.Text.Trim();
            setter = v => box.Text = v;
        }

        bool file = LooksLikeFile(e), folder = LooksLikeFolder(e);
        if (!file && !folder) return (input, getter);

        var dock = new DockPanel();
        var browse = Ui.Button("...", () =>
        {
            if (folder)
            {
                var d = new OpenFolderDialog();
                if (d.ShowDialog() == true) setter(d.FolderName);
            }
            else
            {
                var d = new OpenFileDialog { CheckFileExists = false };
                if (d.ShowDialog() == true) setter(d.FileName);
            }
        }, tip: "Browse").Margin(4, 0, 0, 0);
        browse.Padding = new Thickness(8, 3, 8, 3);
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);
        dock.Children.Add(input);
        return (dock, getter);
    }

    private bool TryBuild(out BuiltCommand command)
    {
        var values = new List<ElementValue>();
        string? problem = null;
        foreach (var r in _rows)
        {
            var e = r.Element;
            bool enabled = e.Kind switch
            {
                ElementKind.Keyword => true,
                ElementKind.Positional => r.Value().Length > 0,
                _ => r.Enabled?.IsChecked == true,
            };
            string value = r.Value();
            if (e.Required && e.Kind == ElementKind.Positional && value.Length == 0)
                problem ??= "Fill in: " + (e.IsVm ? "virtual machine" : Humanize(e.Placeholder));
            if (enabled && e.Kind == ElementKind.Option && e.HasValue && value.Length == 0 && e.Required)
                problem ??= "Give a value for " + e.Name;
            if (e.Indexed && enabled && !int.TryParse(r.Index(), out _))
                problem ??= "Enter a number for " + e.Name;
            values.Add(new ElementValue(e, value, r.Index(), enabled));
        }
        var args = CommandLine.Build(_ctx.Synopsis, values);
        if (_ctx.Doc.Program != null) args.RemoveAt(0);
        command = new BuiltCommand
        {
            Args = args, Title = _ctx.Title, Dangerous = Curation.IsDangerous(args),
            Program = _ctx.Doc.Program, Runnable = _ctx.Doc.Runnable,
        };
        _error.Text = problem ?? "";
        return problem == null;
    }

    private void UpdatePreview()
    {
        if (_rows.Count == 0) return;
        TryBuild(out var c);
        _error.Text = "";
        _preview.Text = c.Display;
        _preview.Foreground = c.Dangerous ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB4, 0xA8)) : Ui.Brush("TermText");
    }

    private void Emit(Action<BuiltCommand>? target)
    {
        if (TryBuild(out var c)) target?.Invoke(c);
    }
}
