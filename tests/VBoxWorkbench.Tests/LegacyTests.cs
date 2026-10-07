using System.Text;
using VBoxWorkbench.Core;
using Xunit;

namespace VBoxWorkbench.Tests;

/// <summary>The usage text of VirtualBox 6.1, taken from the "Commands Overview" of its manual.</summary>
public class LegacyTests
{
    private static readonly Lazy<string> Usage = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-6.1", "usage.txt")));

    private static readonly Lazy<Dictionary<string, List<Synopsis>>> Parsed = new(() => LegacyUsage.Parse(Usage.Value));

    private static Synopsis One(string path, Func<Synopsis, bool>? filter = null) =>
        Parsed.Value.Values.SelectMany(v => v).First(s => s.Path == path && (filter?.Invoke(s) ?? true));

    private static List<string> Build(Synopsis syn, Dictionary<string, string> options, List<string>? positionals = null, string index = "1")
    {
        int p = 0;
        var used = new HashSet<string>();
        return CommandLine.Build(syn, syn.Elements.Select(e => e.Kind switch
        {
            ElementKind.Keyword => new ElementValue(e, "", index),
            ElementKind.Positional when e.IsVm => new ElementValue(e, "vm1"),
            ElementKind.Positional => new ElementValue(e, positionals != null && p < positionals.Count ? positionals[p++] : ""),
            ElementKind.Option => options.TryGetValue(e.Name, out var v) && used.Add(e.Name) ? new ElementValue(e, v, index) : new ElementValue(e, "", Enabled: false),
            _ => new ElementValue(e, "", Enabled: false),
        }));
    }

    [Fact]
    public void Old_and_new_usage_styles_are_told_apart()
    {
        Assert.True(LegacyUsage.LooksLegacy(Usage.Value));
        string modern = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v7.2.20", "_usage.txt"));
        Assert.False(LegacyUsage.LooksLegacy(modern));
        Assert.Empty(LegacyUsage.Parse(modern));
    }

    [Fact]
    public void Every_command_block_becomes_synopses()
    {
        var sb = new StringBuilder();
        foreach (var s in Parsed.Value.Values.SelectMany(v => v))
        {
            sb.Append(s.Path).Append(" ::");
            foreach (var e in s.Elements)
                sb.Append($" {e.Kind.ToString()[0]}{(e.Required ? '!' : '?')}({e.Name}{(e.Indexed ? "#" : "")}{(e.Kind == ElementKind.Option && e.HasValue ? " " + e.Placeholder : "")}{(e.Choices.Count > 0 ? " {" + string.Join(',', e.Choices) + "}" : "")})");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dump-6.1.txt"), sb.ToString());

        foreach (var c in new[] { "list", "showvminfo", "createvm", "modifyvm", "controlvm", "startvm", "storageattach", "storagectl",
                     "bandwidthctl", "createmedium", "modifymedium", "usbfilter", "guestproperty", "guestcontrol", "metrics", "natnetwork", "hostonlyif" })
            Assert.True(Parsed.Value.ContainsKey(c), c);
    }

    [Fact]
    public void Modifyvm_block_yields_old_style_options()
    {
        var opts = One("modifyvm").Options.ToList();
        Assert.True(opts.Count > 120, opts.Count.ToString());
        var nic = opts.First(o => o.Name == "--nic");
        Assert.True(nic.Indexed);
        Assert.False(nic.UsesEquals);
        Assert.Contains("bridged", nic.Choices);
        var mem = opts.First(o => o.Name == "--memory");
        Assert.True(mem.HasValue);
        Assert.Contains(opts, o => o.Name == "--nictype" && o.Indexed);
        Assert.Contains(opts, o => o.Name == "--boot" && o.Indexed && o.Choices.Contains("dvd"));

        Assert.Equal(["modifyvm", "vm1", "--memory", "2048", "--nic2", "bridged"],
            Build(One("modifyvm"), new() { ["--memory"] = "2048", ["--nic"] = "bridged" }, index: "2"));
    }

    [Fact]
    public void Controlvm_block_splits_into_subcommands()
    {
        var paths = Parsed.Value["controlvm"].Select(s => s.Path).ToList();
        foreach (var p in new[] { "controlvm pause", "controlvm resume", "controlvm poweroff", "controlvm savestate", "controlvm acpipowerbutton",
                     "controlvm keyboardputstring", "controlvm setlinkstate", "controlvm nic", "controlvm natpf", "controlvm natpf delete",
                     "controlvm usbattach", "controlvm removeallencpasswords", "controlvm changeuartmode", "controlvm vm-process-priority" })
            Assert.True(paths.Contains(p), p + " missing. Have: " + string.Join(", ", paths));

        Assert.Equal(["controlvm", "vm1", "pause"], Build(One("controlvm pause"), []));
        Assert.Equal(["controlvm", "vm1", "setlinkstate3", "off"], Build(One("controlvm setlinkstate"), [], ["off"], "3"));
        Assert.Equal(["controlvm", "vm1", "natpf1", "ssh,tcp,,2222,,22"],
            Build(One("controlvm natpf", s => !s.Path.EndsWith("delete")), [], ["ssh,tcp,,2222,,22"]));
        Assert.Equal(["controlvm", "vm1", "changeuartmode1", "tcpserver", "5000"], Build(One("controlvm changeuartmode"), [], ["tcpserver 5000"]));
    }

    [Fact]
    public void Blocks_with_trailing_subcommands_and_notes_parse()
    {
        var host = Parsed.Value["hostonlyif"].Select(s => s.Path).ToList();
        Assert.Equal(["hostonlyif ipconfig", "hostonlyif create", "hostonlyif remove"], host);
        Assert.Contains(One("hostonlyif ipconfig").Options, o => o.Name == "--ip" && o.HasValue);

        var bw = Parsed.Value["bandwidthctl"].Select(s => s.Path).ToList();
        Assert.Equal(["bandwidthctl add", "bandwidthctl set", "bandwidthctl remove", "bandwidthctl list"], bw);
        Assert.Equal(["bandwidthctl", "vm1", "add", "Limit", "--type", "network", "--limit", "20m"],
            Build(One("bandwidthctl add"), new() { ["--type"] = "network", ["--limit"] = "20m" }, ["Limit"]));

        var filter = One("usbfilter add");
        var target = filter.Options.First(o => o.Name == "--target");
        Assert.Contains("global", target.Choices);
        Assert.Equal(["usbfilter", "add", "0", "--target", "vm1", "--name", "stick"],
            Build(filter, new() { ["--target"] = "vm1", ["--name"] = "stick" }, ["0"]));
    }

    private static readonly Lazy<Catalog> Old = new(() => Catalog.FromHelpFiles("6.1", Usage.Value, []));

    [Fact]
    public void Typos_and_sibling_usages_in_the_old_text_are_handled()
    {
        var ctl = One("storagectl").Options.Select(o => o.Name).ToList();
        foreach (var o in new[] { "--name", "--add", "--controller", "--portcount", "--hostiocache", "--bootable", "--rename", "--remove" })
            Assert.Contains(o, ctl);
        Assert.Equal(["mediumproperty set", "mediumproperty get", "mediumproperty delete"], Parsed.Value["mediumproperty"].Select(s => s.Path));
    }

    [Fact]
    public void Old_catalog_borrows_descriptions_but_keeps_its_own_syntax()
    {
        var modify = Old.Value.Find("modifyvm")!;
        Assert.True(modify.Legacy);
        Assert.Equal("Change settings for a virtual machine that is stopped", modify.Summary);
        var nictype = modify.Synopses[0].Options.First(o => o.Name == "--nictype");
        Assert.Contains("networking hardware", modify.HelpFor(nictype));
        Assert.Contains("RAM", modify.HelpFor(modify.Synopses[0].Options.First(o => o.Name == "--memory")));
        Assert.Equal("Pause a Virtual Machine", SearchIndex.TitleOf(Old.Value.Find("controlvm")!, One("controlvm pause")));
        int described = modify.Synopses[0].Options.Count(o => modify.HelpFor(o).Length > 0);
        Assert.True(described > 100, described + " of " + modify.Synopses[0].Options.Count());
    }

    [Fact]
    public void Old_option_spellings_land_in_the_right_zone()
    {
        Assert.Equal(Zone.Ports, Curation.ZoneOfOption("--nictype"));
        Assert.Equal(Zone.Ports, Curation.ZoneOfOption("--natpf"));
        Assert.Equal(Zone.Ports, Curation.ZoneOfOption("--macaddress"));
        Assert.Equal(Zone.Board, Curation.ZoneOfOption("--pagefusion"));
        Assert.Equal(Zone.Board, Curation.ZoneOfOption("--longmode"));
        Assert.Equal(Zone.Board, Curation.ZoneOfOption("--x86-long-mode"));
        Assert.Equal(Zone.Sharing, Curation.ZoneOfOption("--draganddrop"));
        Assert.Equal(Zone.Display, Curation.ZoneOfOption("--videocap"));
        Assert.Equal(Zone.General, Curation.ZoneOfOption("--ostype"));
        var opts = Old.Value.Find("modifyvm")!.Synopses[0].Options.ToList();
        int unplaced = opts.Count(o => Curation.ZoneOfOption(o.Name) == Zone.More);
        Assert.True(unplaced <= 3, string.Join(' ', opts.Where(o => Curation.ZoneOfOption(o.Name) == Zone.More).Select(o => o.Name)));
    }

    [Fact]
    public void Plain_language_search_works_on_the_old_catalog()
    {
        var index = new SearchIndex(Old.Value);
        var forward = index.Search("forward port", 5).First(e => e.Curated && e.Synopsis.Path == "modifyvm");
        Assert.Equal("--natpf", forward.FocusOption);
        Assert.Equal("ssh,tcp,,2222,,22", forward.Presets!["--natpf"]);
        var clip = index.Search("clipboard", 5).First(e => e.Curated);
        Assert.True(clip.Synopsis.Options.Any(o => o.Name == clip.FocusOption),
            $"{clip.Title} -> {clip.Synopsis.Path} focus {clip.FocusOption}; has {string.Join(' ', clip.Synopsis.Options.Select(o => o.Name).Where(n => n.Contains("clip")))}");
        Assert.Contains(index.Search("ram", 5), e => e.Synopsis.Path == "modifyvm");
        Assert.Contains(index.Search("pause", 5), e => e.Synopsis.Path == "controlvm pause");
        int curated = index.Entries.Count(e => e.Curated);
        Assert.True(curated >= 45, "only " + curated + " phrases resolved on 6.1");
    }

    [Fact]
    public void List_block_stays_one_action_with_choices()
    {
        var list = Assert.Single(Parsed.Value["list"]);
        var what = list.Elements.Last(e => e.Kind == ElementKind.Positional);
        Assert.Contains("runningvms", what.Choices);
        Assert.Contains("hdds", what.Choices);
    }
}
