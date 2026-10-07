using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VBoxWorkbench.Core;

namespace VBoxWorkbench.App;

/// <summary>The opened machine: a circuit board whose parts are the VM's settings.</summary>
public partial class MainWindow
{
    private async Task ShowWorkbenchAsync(VmRef vm)
    {
        if (_runner == null) return;
        _currentVm = vm;
        var r = await _runner.RunAsync(["showvminfo", vm.Uuid, "--machinereadable"]);
        _info = VmInfo.Parse(r.Output);
        if (_info.Name.Length == 0)
        {
            // An inaccessible or encrypted VM still gets a bench, with whatever VirtualBox told us.
            _info.Props["name"] = vm.Name;
            _info.Props["UUID"] = vm.Uuid;
        }
        BuildNav();
        _back.Clear();
        SetContent(BuildWorkbench(_info, r.Ok ? null : r.Combined), () => _ = ShowWorkbenchAsync(vm), shelf: true);
        await BuildShelfAsync();
    }

    private BuiltCommand Direct(string title, params string[] args) =>
        new() { Args = [.. args], Title = title, Dangerous = Curation.IsDangerous(args) };

    private Button Chip(string label, Action onClick, bool filled = true, string? tip = null, string glyph = "") =>
        Ui.Button(label, onClick, filled ? "Chip" : "Socket", glyph, tip);

    private CommandDoc? Modify => _catalog.Find("modifyvm");

    /// <summary>All modifyvm options of one zone as a single form, whichever usage line they came from.</summary>
    private Synopsis? ZoneSynopsis(Zone zone)
    {
        var doc = Modify;
        if (doc == null) return null;
        var syn = new Synopsis { Command = "modifyvm", Raw = "modifyvm" };
        var vm = doc.Synopses.SelectMany(s => s.Elements).FirstOrDefault(e => e.IsVm);
        if (vm != null) syn.Elements.Add(vm);
        var seen = new HashSet<string>();
        foreach (var o in doc.Synopses.SelectMany(s => s.Options))
            if (Curation.ZoneOfOption(o.Name) == zone && seen.Add(o.Name + "=" + o.Placeholder)) syn.Elements.Add(o);
        return syn.Options.Any() ? syn : null;
    }

    private void OpenModify(string option, string value = "", string? index = null, Dictionary<string, string>? more = null)
    {
        var doc = Modify;
        var zone = Curation.ZoneOfOption(option);
        var syn = ZoneSynopsis(zone);
        // The board asks for options by their 7.x names; older versions spell them without dashes.
        if (syn != null && OptionNames.Find(syn, option) is { } actual) option = actual.Name;
        if (doc == null || syn == null || !syn.Options.Any(o => o.Name == option))
        {
            AppendOutput($"Your VirtualBox version has no modifyvm {option} option. Use the search bar to find its new name.");
            ShowOutput(true);
            return;
        }
        var presets = new Dictionary<string, string> { [option] = value };
        if (more != null) foreach (var kv in more) presets[kv.Key] = kv.Value;
        OpenForm(doc, syn, Curation.ZoneTitle(zone) + " settings", option, presets, index: index);
    }

    /// <summary>A menu with every action of one command, titled the way the installed help titles them.</summary>
    private void CommandMenu(FrameworkElement anchor, IEnumerable<(CommandDoc Doc, Synopsis Syn)> actions)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (doc, syn) in actions)
        {
            var item = new MenuItem { Header = SearchIndex.TitleOf(doc, syn), ToolTip = syn.Raw };
            item.Click += (_, _) => OpenForm(doc, syn);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.IsOpen = true;
    }

    private IEnumerable<(CommandDoc, Synopsis)> ActionsOf(string command) =>
        _catalog.Find(command) is { } doc ? doc.Synopses.Select(s => (doc, s)) : [];

    private FrameworkElement BuildWorkbench(VmInfo info, string? problem)
    {
        var root = new StackPanel();

        // ---- header and power ----
        var header = new DockPanel { Margin = new Thickness(0, 0, 12, 12) };
        var power = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(power, Dock.Right);
        void Now(string label, string glyph, string style, params string[] args) =>
            power.Children.Add(Ui.Button(label, async () => await RunAsync([Direct(label, args)]), style, glyph));
        string n = info.Name;
        if (info.IsRunning)
        {
            if (info.IsPaused) Now("Resume", Ui.IcoPlay, "PrimaryButton", "controlvm", n, "resume");
            else Now("Pause", Ui.IcoPause, "FlatButton", "controlvm", n, "pause");
            Now("Save state", Ui.IcoSave, "FlatButton", "controlvm", n, "savestate");
            Now("Shut down", Ui.IcoPower, "FlatButton", "controlvm", n, "acpipowerbutton");
            Now("Reset", Ui.IcoRefresh, "FlatButton", "controlvm", n, "reset");
            Now("Power off", Ui.IcoStop, "FlatButton", "controlvm", n, "poweroff");
        }
        else
        {
            Now(info.IsSaved ? "Resume from saved state" : "Start", Ui.IcoPlay, "PrimaryButton", "startvm", n, "--type", "gui");
            Now("Start in background", Ui.IcoPlay, "FlatButton", "startvm", n, "--type", "headless");
            if (info.IsSaved) Now("Discard saved state", Ui.IcoDelete, "FlatButton", "discardstate", n);
        }
        header.Children.Add(power);
        var title = Ui.Row(Ui.Text(n, 24, bold: true),
            new Border
            {
                Background = Ui.Brush(info.IsRunning ? "Ok" : "Muted"), CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3),
                Margin = new Thickness(12, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text(info.State, 12).With(t => t.Foreground = System.Windows.Media.Brushes.White),
            });
        header.Children.Add(Ui.Column(title, Ui.Text($"{info.Get("ostype")}  ·  {info.Get("CfgFile")}", 12, "Muted")));
        root.Children.Add(header);
        if (problem != null) root.Children.Add(Ui.Text(problem, 12.5, "Danger", wrap: true).Margin(0, 0, 0, 10));
        if (!info.CanModify)
            root.Children.Add(Ui.Row(Ui.Icon(Ui.IcoLock, 13, "Copper"),
                Ui.Text($"{n} is {info.State}. Parts that need the power off will say so; live actions work now.", 12.5, "Muted")).Margin(0, 0, 0, 10));

        // ---- the board ----
        // Two columns of cards; each new card goes to the shorter column.
        var board = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        board.ColumnDefinitions.Add(new ColumnDefinition());
        board.ColumnDefinitions.Add(new ColumnDefinition());
        var columns = new[] { new StackPanel(), new StackPanel() };
        Grid.SetColumn(columns[1], 1);
        board.Children.Add(columns[0]);
        board.Children.Add(columns[1]);
        var weight = new int[2];
        var controls = (_catalog.Find("controlvm") is { } cdoc ? cdoc.Synopses.Select(s => (Doc: cdoc, Syn: s)) : []).ToList();

        void ZoneCard(Zone zone, string glyph, params UIElement[] parts)
        {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Row(Ui.Icon(glyph, 15, "Gold"), Ui.Text(Curation.ZoneTitle(zone), 14.5, "PcbText", bold: true)).Margin(0, 0, 0, 8));
            var wrap = new WrapPanel();
            foreach (var p in parts) wrap.Children.Add(p);
            panel.Children.Add(wrap);

            var foot = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            var zs = ZoneSynopsis(zone);
            if (zs != null && Modify is { } mdoc)
                foot.Children.Add(Chip($"All {zs.Options.Count()} settings", () => OpenForm(mdoc, zs, Curation.ZoneTitle(zone) + " settings"), false,
                    "Every modifyvm option in this area, from your installed VirtualBox"));
            var live = controls.Where(c => Curation.ZoneOfControl(c.Syn) == zone).ToList();
            if (live.Count > 0)
            {
                Button? b = null;
                b = Chip($"{live.Count} live actions", () => CommandMenu(b!, live), false, "controlvm actions for a running machine", Ui.IcoPlay);
                foot.Children.Add(b);
            }
            if (foot.Children.Count > 0) panel.Children.Add(foot);
            int col = weight[0] <= weight[1] ? 0 : 1;
            weight[col] += 3 + parts.Length;
            columns[col].Children.Add(new Border
            {
                Child = panel, Background = Ui.Brush("Pcb"), BorderBrush = Ui.Brush("Trace"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 8, 8), Margin = new Thickness(0, 0, 12, 12),
            });
        }

        // Identity
        ZoneCard(Zone.General, Ui.IcoBox,
            Chip("Name: " + n, () => OpenModify("--name", n)),
            Chip("OS: " + info.Get("ostype", "unknown"), () => OpenModify("--os-type")),
            Chip("Groups: " + info.Get("groups", "/"), () => OpenModify("--groups", info.Get("groups", "/"))),
            Chip("Description", () => OpenModify("--description"), false),
            Chip("Autostart: " + info.Get("autostart-enabled", "off"), () => OpenModify("--autostart-enabled", "on"), info.Get("autostart-enabled") == "on"));

        // Board
        var boardParts = new List<UIElement>();
        int cpus = Math.Max(info.GetInt("cpus", 1), 1);
        for (int i = 0; i < Math.Min(cpus, 16); i++)
            boardParts.Add(Chip("CPU " + i, () => OpenModify("--cpus", cpus.ToString()), tip: $"{cpus} virtual CPUs. Click to change the count.", glyph: Ui.IcoChip));
        boardParts.Add(Chip("+", () => OpenModify("--cpus", (cpus + 1).ToString()), false, "Add a CPU"));
        boardParts.Add(Chip($"{info.Get("memory", "?")} MB RAM", () => OpenModify("--memory", info.Get("memory"))));
        boardParts.Add(Chip("Firmware: " + info.Get("firmware", "BIOS"), () => OpenModify("--firmware", info.Get("firmware").ToLowerInvariant())));
        boardParts.Add(Chip("Chipset: " + info.Get("chipset", "?"), () => OpenModify("--chipset", info.Get("chipset"))));
        var boot = Enumerable.Range(1, 4).Select(i => info.Get("boot" + i)).Where(b => b is not ("" or "none"));
        boardParts.Add(Chip("Boot: " + string.Join(", ", boot.DefaultIfEmpty("none")), () => OpenModify("--boot", info.Get("boot1"), "1")));
        boardParts.Add(Chip("Nested VT: " + info.Get("nested-hw-virt", "off"), () => OpenModify("--nested-hw-virt", "on"), info.Get("nested-hw-virt") == "on"));
        boardParts.Add(Chip("Paravirt: " + info.Get("paravirtprovider", "default"), () => OpenModify("--paravirt-provider", info.Get("paravirtprovider"))));
        ZoneCard(Zone.Board, Ui.IcoChip, [.. boardParts]);

        // Drive bays
        var bays = new List<UIElement>();
        foreach (var ctl in info.Controllers())
        {
            bays.Add(Ui.Text($"{ctl.Name}  ({ctl.Type})", 12, "PcbMuted").With(t => { t.Width = 2000; t.Margin = new Thickness(0, 2, 0, 4); }));
            foreach (var slot in ctl.Slots)
            {
                var s = slot;
                var b = Chip($"{s.Port}:{s.Device}  {s.Label}", () => OpenAttach(s, null), !s.Empty,
                    s.Empty ? "Empty. Click, or drop a disk or ISO from the shelf." : s.Medium, s.Empty ? "" : Ui.IcoDisk);
                b.AllowDrop = true;
                b.Drop += (_, e) => { if (e.Data.GetData(DataFormats.StringFormat) is string path) OpenAttach(s, path); };
                bays.Add(b);
            }
        }
        if (bays.Count == 0) bays.Add(Ui.Text("No storage controller yet. Add one to attach disks.", 12.5, "PcbMuted"));
        bays.Add(Chip("+ controller", () => OpenForm("storagectl", "Add or change a storage controller", "--add", new() { ["--add"] = "sata", ["--name"] = "SATA" }), false));
        bays.Add(Chip("Attach anything", () => OpenForm("storageattach"), false, "Open the full storageattach form"));
        bays.Add(Chip("Speed limits", () => OpenForm("bandwidthctl add"), false, "Bandwidth groups for disks and network"));
        ZoneCard(Zone.Bays, Ui.IcoDisk, [.. bays]);

        // Ports
        var ports = new List<UIElement>();
        bool shownEmpty = false;
        foreach (var nic in info.Nics())
        {
            bool on = nic.Mode != "none";
            if (!on && shownEmpty) continue;
            if (!on) shownEmpty = true;
            var k = nic;
            ports.Add(Chip(on ? $"NIC {k.Index}: {k.Mode}{(k.Detail.Length > 0 ? " · " + k.Detail : "")}" : $"NIC {k.Index}: empty", () => OpenNic(k), on,
                on ? "Click to rewire this adapter" : "Plug this adapter into a network", Ui.IcoPlug));
        }
        ports.Add(Chip("Port forwarding", () =>
        {
            if (info.IsRunning) OpenForm("controlvm natpf", index: "1", filter: s => !s.Path.EndsWith("delete"));
            else OpenModify("--nat-pf", "ssh,tcp,,2222,,22", "1");
        }, false, "Reach a service in the VM through a port on this computer (NAT)"));
        string usb = info.Get("xhci") == "on" ? "USB 3" : info.Get("ehci") == "on" ? "USB 2" : info.Get("usb") == "on" ? "USB 1.1" : "USB off";
        ports.Add(Chip(usb, () => OpenModify("--usb-xhci", "on"), usb != "USB off", glyph: Ui.IcoUsb));
        ports.Add(Chip("Audio: " + info.Get("audio", "none"), () => OpenModify("--audio-enabled", "on"), info.Get("audio") != "none"));
        ports.Add(Chip("Serial", () => OpenModify("--uart", "0x3F8 4", "1"), info.Get("uart1", "off") != "off"));
        ports.Add(Chip("Mouse: " + info.Get("hidpointing", "?"), () => OpenModify("--mouse", "usbtablet")));
        ZoneCard(Zone.Ports, Ui.IcoPlug, [.. ports]);

        // Display
        ZoneCard(Zone.Display, Ui.IcoScreen,
            Chip($"{info.Get("monitorcount", "1")} monitor(s)", () => OpenModify("--monitor-count", info.Get("monitorcount", "1"))),
            Chip(info.Get("graphicscontroller", "graphics"), () => OpenModify("--graphicscontroller", info.Get("graphicscontroller"))),
            Chip($"{info.Get("vram", "?")} MB video", () => OpenModify("--vram", info.Get("vram"))),
            Chip("3D: " + info.Get("accelerate3d", "off"), () => OpenModify("--accelerate-3d", "on"), info.Get("accelerate3d") == "on"),
            Chip("Recording: " + info.Get("recording_enabled", "off"), () => OpenModify("--recording", "on"), info.Get("recording_enabled") == "on"),
            Chip("Remote desktop: " + info.Get("vrde", "off"), () => OpenModify("--vrde", "on"), info.Get("vrde") == "on"),
            Chip("Screenshot", () => OpenForm("controlvm screenshotpng"), false, "Needs a running machine", Ui.IcoCamera));

        // Sharing
        var sharing = new List<UIElement>
        {
            Chip("Clipboard: " + info.Get("clipboard", "disabled"), () =>
            {
                if (info.IsRunning) OpenForm("controlvm clipboard mode", positionals: ["bidirectional"]);
                else OpenModify("--clipboard-mode", "bidirectional");
            }, info.Get("clipboard", "disabled") != "disabled"),
            Chip("Drag and drop: " + info.Get("draganddrop", "disabled"), () =>
            {
                if (info.IsRunning) OpenForm("controlvm draganddrop", positionals: ["bidirectional"]);
                else OpenModify("--drag-and-drop", "bidirectional");
            }, info.Get("draganddrop", "disabled") != "disabled"),
        };
        for (int i = 1; info.Props.ContainsKey("SharedFolderNameMachineMapping" + i); i++)
        {
            string share = info.Get("SharedFolderNameMachineMapping" + i);
            sharing.Add(Chip("Folder: " + share, () => OpenForm("sharedfolder remove", presets: new() { ["--name"] = share }),
                tip: info.Get("SharedFolderPathMachineMapping" + i) + "\nClick to remove", glyph: Ui.IcoFolder));
        }
        sharing.Add(Chip("+ shared folder", () => OpenForm("sharedfolder add", presets: new() { ["--name"] = "", ["--hostpath"] = "", ["--automount"] = "" }), false));
        sharing.Add(Chip("+ USB filter", () => OpenForm("usbfilter add", presets: new() { ["--target"] = n, ["--name"] = "" }, positionals: ["0"]), false));
        ZoneCard(Zone.Sharing, Ui.IcoShare, [.. sharing]);

        // Vault and advanced
        Button? nvram = null, enc = null;
        nvram = Chip("Secure boot: " + info.Get("SecureBoot", "off"), () => CommandMenu(nvram!, ActionsOf("modifynvram")), info.Get("SecureBoot") == "on",
            "UEFI variable store and secure boot keys (modifynvram)");
        enc = Chip("Encryption: " + info.Get("encryption", "disabled"), () => CommandMenu(enc!, ActionsOf("encryptvm")), info.Get("encryption", "disabled") != "disabled",
            "Encrypt the whole VM and manage its passwords (encryptvm)", Ui.IcoLock);
        ZoneCard(Zone.Vault, Ui.IcoShield, enc, nvram,
            Chip("TPM: " + info.Get("tpm-type", "none"), () => OpenModify("--tpm-type", "2.0"), info.Get("tpm-type", "none") != "none"),
            Chip("Teleport target: " + info.Get("teleporterenabled", "off"), () => OpenModify("--teleporter", "on"), info.Get("teleporterenabled") == "on"));

        // Anything the zone rules don't know (options from newer versions) still gets a home.
        if (ZoneSynopsis(Zone.More) != null || controls.Any(c => Curation.ZoneOfControl(c.Syn) == Zone.More))
            ZoneCard(Zone.More, Ui.IcoMore, Ui.Text("Settings and actions without a dedicated spot.", 12.5, "PcbMuted"));

        root.Children.Add(board);

        // ---- timeline ----
        var time = new WrapPanel();
        var snaps = info.Snapshots();
        foreach (var s in snaps)
        {
            var snap = s;
            Button? b = null;
            b = Chip((snap.Depth > 0 ? new string('·', snap.Depth) + " " : "") + snap.Name + (snap.Current ? "  (you are here)" : ""), () =>
            {
                var menu = new ContextMenu { PlacementTarget = b };
                void Item(string label, string path, List<string>? pos = null)
                {
                    var mi = new MenuItem { Header = label };
                    mi.Click += (_, _) => OpenForm(path, positionals: pos ?? [snap.Name]);
                    menu.Items.Add(mi);
                }
                Item("Go back to this snapshot", "snapshot restore");
                Item("Rename or describe", "snapshot edit");
                Item("Show the settings it holds", "snapshot showvminfo");
                Item("Clone a machine from it", "clonevm", []);
                Item("Delete this snapshot", "snapshot delete");
                menu.IsOpen = true;
            }, snap.Current, glyph: Ui.IcoBranch);
            time.Children.Add(b);
        }
        if (snaps.Count == 0) time.Children.Add(Ui.Text("No snapshots yet. A snapshot lets you come back to this exact moment.", 12.5, "PcbMuted").Margin(0, 0, 12, 6));
        time.Children.Add(Chip("Take snapshot", () => OpenForm("snapshot take", presets: info.IsRunning ? new() { ["--live"] = "" } : null), false, glyph: Ui.IcoAdd));
        if (snaps.Count > 0) time.Children.Add(Chip("Undo back to current snapshot", () => OpenForm("snapshot restorecurrent"), false));
        root.Children.Add(new Border
        {
            Background = Ui.Brush("Pcb"), BorderBrush = Ui.Brush("Trace"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 8, 8), Margin = new Thickness(0, 0, 12, 12),
            Child = Ui.Column(Ui.Row(Ui.Icon(Ui.IcoBranch, 15, "Gold"), Ui.Text("Timeline", 14.5, "PcbText", bold: true)).Margin(0, 0, 0, 8), time),
        });

        // ---- everything else for this machine ----
        var every = new WrapPanel();
        foreach (var name in Curation.MachineScoped)
        {
            var doc = _catalog.Find(name);
            if (doc == null) continue;
            Button? b = null;
            b = Ui.Button(name, () =>
            {
                if (doc.Synopses.Count == 1) OpenForm(doc, doc.Synopses[0]);
                else CommandMenu(b!, doc.Synopses.Select(s => (doc, s)));
            }, tip: doc.Summary);
            every.Children.Add(b);
        }
        root.Children.Add(Ui.Card(Ui.Column(
            Ui.Text("Every command for this machine", 14.5, bold: true),
            Ui.Text("The complete list from your VirtualBox, for anything the board doesn't show as a part.", 12.5, "Muted", wrap: true).Margin(0, 2, 0, 8),
            every)));

        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Tag = "workbench" };
    }

    private bool HasOpt(string option) => Modify?.Synopses.Any(s => OptionNames.Find(s, option) != null) == true;

    private void OpenAttach(StorageSlot slot, string? medium)
    {
        var presets = new Dictionary<string, string>
        {
            ["--storagectl"] = slot.Controller, ["--port"] = slot.Port.ToString(), ["--device"] = slot.Device.ToString(),
        };
        if (medium != null)
        {
            presets["--medium"] = medium;
            presets["--type"] = Path.GetExtension(medium).Equals(".iso", StringComparison.OrdinalIgnoreCase) ? "dvddrive" : "hdd";
        }
        else presets["--medium"] = slot.Empty ? "" : slot.Medium;
        OpenForm("storageattach", slot.Empty ? "Put something in this bay" : "Change or remove what's in this bay", "--medium", presets);
    }

    private void OpenNic(Nic nic, string? mode = null, string? network = null)
    {
        string idx = nic.Index.ToString();
        if (_info?.IsRunning == true)
        {
            OpenForm("controlvm nic", "Rewire a network adapter (live)", index: idx, positionals: [mode ?? "nat", network ?? ""]);
            return;
        }
        var more = new Dictionary<string, string>();
        string m = mode ?? (nic.Mode == "none" ? "nat" : nic.Mode);
        string? detailOption = m switch
        {
            "bridged" => "--bridge-adapter", "intnet" => "--intnet", "hostonly" => "--host-only-adapter",
            "natnetwork" => "--nat-network", "hostonlynet" => "--host-only-net", _ => null,
        };
        if (detailOption != null && HasOpt(detailOption)) more[detailOption] = network ?? nic.Detail;
        OpenModify("--nic", m, idx, more);
    }

    // ---------- parts shelf ----------

    private async Task BuildShelfAsync()
    {
        if (_runner == null) return;
        ShelfPanel.Children.Clear();
        ShelfPanel.Children.Add(Ui.Text("Parts shelf", 15, bold: true));
        ShelfPanel.Children.Add(Ui.Text("Click a part to use it on this machine, or drag a disk onto a bay.", 12, "Muted", wrap: true).Margin(0, 2, 0, 10));

        var hdds = _runner.RunAsync(["list", "hdds"]);
        var dvds = _runner.RunAsync(["list", "dvds"]);
        var intnets = _runner.RunAsync(["list", "intnets"]);
        var natnets = _runner.RunAsync(["list", "natnets"]);
        var hostonly = _runner.RunAsync(["list", "hostonlyifs"]);
        var bridged = _runner.RunAsync(["list", "bridgedifs"]);
        var usb = _runner.RunAsync(["list", "usbhost"]);
        await Task.WhenAll(hdds, dvds, intnets, natnets, hostonly, bridged, usb);

        void Section(string title) => ShelfPanel.Children.Add(Ui.Text(title, 11.5, "Muted").Margin(0, 8, 0, 4));
        void Part(string label, string glyph, string tip, Action click, string? dragData = null)
        {
            var b = Ui.Button(label, click, "NavButton", glyph, tip);
            b.Padding = new Thickness(6, 5, 6, 5);
            if (dragData != null)
            {
                // Start a drag only after the pointer has really moved, so a plain click still clicks.
                Point? down = null;
                b.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(b);
                b.PreviewMouseLeftButtonUp += (_, _) => down = null;
                b.PreviewMouseMove += (_, e) =>
                {
                    if (down is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
                    var now = e.GetPosition(b);
                    if (Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                        Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    down = null;
                    DragDrop.DoDragDrop(b, dragData, DragDropEffects.Copy);
                };
            }
            ShelfPanel.Children.Add(b);
        }
        StorageSlot FreeSlot(bool dvd)
        {
            var slots = _info?.Controllers().SelectMany(c => c.Slots).ToList() ?? [];
            return slots.FirstOrDefault(s => dvd ? s.Medium == "emptydrive" : s.Empty) ?? slots.FirstOrDefault(s => s.Empty) ?? new StorageSlot("", 0, 0, "none");
        }

        Section("Disks");
        foreach (var d in VmInfo.ParseBlocks(hdds.Result.Output).Where(d => d.ContainsKey("Location")).Take(30))
        {
            string loc = d["Location"];
            Part(Path.GetFileName(loc), Ui.IcoDisk, $"{loc}\n{d.GetValueOrDefault("Capacity")}  {d.GetValueOrDefault("Storage format")}  {d.GetValueOrDefault("State")}",
                () => OpenAttach(FreeSlot(false), loc), loc);
        }
        Part("New disk", Ui.IcoAdd, "Create a new virtual hard disk", () => OpenForm("createmedium", presets: new() { ["--filename"] = "", ["--size"] = "20480" }));

        Section("Discs");
        foreach (var d in VmInfo.ParseBlocks(dvds.Result.Output).Where(d => d.ContainsKey("Location")).Take(30))
        {
            string loc = d["Location"];
            Part(Path.GetFileName(loc), Ui.IcoDisc, loc, () => OpenAttach(FreeSlot(true), loc), loc);
        }
        Part("Guest Additions CD", Ui.IcoDisc, "Insert the Guest Additions disc", () => OpenAttach(FreeSlot(true), "additions"));

        Section("Networks");
        var firstFree = _info?.Nics().FirstOrDefault(x => x.Mode == "none") ?? new Nic(1, "none", "");
        void Nets(RunResult r, string key, string mode, string label)
        {
            foreach (var d in VmInfo.ParseBlocks(r.Output).Where(d => d.ContainsKey(key)).Take(12))
            {
                string name = d[key];
                Part(name, Ui.IcoNet, $"{label}\nClick to plug NIC {firstFree.Index} into it", () => OpenNic(firstFree, mode, name));
            }
        }
        Part("NAT (internet through the host)", Ui.IcoNet, "The simplest way to get the VM online", () => OpenNic(firstFree, "nat"));
        Nets(intnets.Result, "Name", "intnet", "Internal network: VMs only");
        Nets(natnets.Result, "Name", "natnetwork", "NAT network: VMs see each other and the internet");
        Nets(natnets.Result, "NetworkName", "natnetwork", "NAT network: VMs see each other and the internet");
        Nets(hostonly.Result, "Name", "hostonly", "Host-only: VMs and this computer");
        Nets(bridged.Result, "Name", "bridged", "Bridged: the VM appears on your real network");

        Section("USB devices on this computer");
        var devices = VmInfo.ParseBlocks(usb.Result.Output).Where(d => d.ContainsKey("UUID")).Take(12).ToList();
        foreach (var d in devices)
        {
            string label = (d.GetValueOrDefault("Product") ?? d.GetValueOrDefault("Manufacturer") ?? "USB device");
            string vendor = (d.GetValueOrDefault("VendorId") ?? "").Split(' ')[0].Replace("0x", "");
            string product = (d.GetValueOrDefault("ProductId") ?? "").Split(' ')[0].Replace("0x", "");
            Part(label, Ui.IcoUsb, $"{d.GetValueOrDefault("Manufacturer")} {vendor}:{product}\n{d.GetValueOrDefault("Current State")}", () =>
            {
                if (_info?.IsRunning == true) OpenForm("controlvm usbattach", positionals: [d["UUID"]]);
                else OpenForm("usbfilter add", "Auto-attach this USB device", positionals: ["0"],
                    presets: new() { ["--target"] = _info?.Name ?? "", ["--name"] = label, ["--vendorid"] = vendor, ["--productid"] = product });
            });
        }
        if (devices.Count == 0) ShelfPanel.Children.Add(Ui.Text("None listed.", 12, "Muted").Margin(6, 0, 0, 0));
    }
}
