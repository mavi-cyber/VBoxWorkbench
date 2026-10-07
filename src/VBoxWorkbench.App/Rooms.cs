using System.Windows;
using System.Windows.Controls;
using VBoxWorkbench.Core;

namespace VBoxWorkbench.App;

/// <summary>Rooms hold the commands that don't belong to one machine. Each lists every action of its commands.</summary>
public partial class MainWindow
{
    private static readonly Dictionary<Room, string[]> Looks = new()
    {
        [Room.Machines] = ["vms", "runningvms", "groups", "ostypes"],
        [Room.Media] = ["hdds", "dvds", "floppies", "hddbackends", "hostdrives", "hostdvds", "hostfloppies"],
        [Room.Network] = ["bridgedifs", "hostonlyifs", "hostonlynets", "intnets", "natnets", "dhcpservers", "cloudnets"],
        [Room.Cloud] = ["cloudproviders", "cloudprofiles"],
        [Room.Guest] = [],
        [Room.Diagnostics] = [],
    };

    private static readonly string[] MachinesFirst = ["createvm", "unattended", "import", "registervm", "clonevm", "export"];

    private void ShowRoom(Room room)
    {
        _back.Clear();
        var root = new StackPanel();
        root.Children.Add(Ui.Text(room == Room.Machines ? "New, import, add" : Curation.RoomTitle(room), 24, bold: true));
        root.Children.Add(Ui.Text(Curation.RoomBlurb(room), 13, "Muted", wrap: true).Margin(0, 2, 12, 12));
        if (room is Room.Guest or Room.Diagnostics && _currentVm != null)
            root.Children.Add(Ui.Text($"Forms here start with {_currentVm.Name} filled in. Pick another machine on the left to change that.", 12.5, "Copper", wrap: true).Margin(0, 0, 12, 12));

        // What exists right now: the "list" subcommands that belong to this room, straight from the installed version.
        var listChoices = _catalog.Find("list")?.Synopses.SelectMany(s => s.Elements)
            .Where(e => e.Kind == ElementKind.Positional).SelectMany(e => e.Choices).Distinct().ToList() ?? [];
        var claimed = Looks.Values.SelectMany(v => v).ToHashSet();
        var looks = room == Room.Host ? listChoices.Where(c => !claimed.Contains(c)).ToList()
            : Looks.TryGetValue(room, out var mine) ? mine.Where(listChoices.Contains).ToList() : [];
        if (looks.Count > 0)
        {
            var wrap = new WrapPanel();
            foreach (var what in looks)
                wrap.Children.Add(Ui.Button(what, async () => await LookAsync("list", "--long", what), glyph: Ui.IcoSearch, tip: "VBoxManage list --long " + what));
            root.Children.Add(Ui.Card(Ui.Column(Ui.Text("Look at what exists", 14.5, bold: true).Margin(0, 0, 0, 8), wrap)));
        }

        var docs = _catalog.Commands.Where(c => Curation.RoomOf(c.Name) == room).ToList();
        if (room == Room.Machines)
            docs = docs.OrderBy(d => { int i = Array.IndexOf(MachinesFirst, d.Name); return i < 0 ? 99 : i; }).ToList();
        foreach (var doc in docs)
        {
            var wrap = new WrapPanel();
            var titles = doc.Synopses.Select(s => SearchIndex.TitleOf(doc, s)).ToList();
            for (int i = 0; i < doc.Synopses.Count; i++)
            {
                var syn = doc.Synopses[i];
                string title = titles[i];
                // Several usage lines can share a title (modifyvm has one per settings group): tell them apart.
                if (titles.Count(t => t == title) > 1)
                {
                    string first = syn.Options.FirstOrDefault()?.Name ?? syn.Elements.LastOrDefault()?.Placeholder ?? (i + 1).ToString();
                    title = $"{title} ({first}...)";
                }
                wrap.Children.Add(Ui.Button(title, () => OpenForm(doc, syn), tip: syn.Raw));
            }
            var head = new DockPanel();
            var manual = Ui.Button("Manual", () => ShowManual(doc), glyph: Ui.IcoBook).Margin(0, 0, 0, 0);
            DockPanel.SetDock(manual, Dock.Right);
            head.Children.Add(manual);
            head.Children.Add(Ui.Column(
                Ui.Text(doc.Name, 14.5, "Copper", bold: true, mono: true),
                Ui.Text(doc.Summary + (doc.Runnable ? "" : "  ·  not available on this host"), 12.5, "Muted", wrap: true).Margin(0, 1, 0, 8)));
            root.Children.Add(Ui.Card(Ui.Column(head, wrap)));
        }
        if (docs.Count == 0) root.Children.Add(Ui.Text("Your VirtualBox version has no commands for this room.", 13, "Muted"));

        void Show() => ShowRoom(room);
        SetContent(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Show);
        BuildNav();
    }
}
