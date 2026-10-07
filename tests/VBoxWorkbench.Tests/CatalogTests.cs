using VBoxWorkbench.Core;
using Xunit;

namespace VBoxWorkbench.Tests;

public class CatalogTests
{
    private static readonly Lazy<Catalog> Installed = new(() =>
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "v7.2.20");
        var helps = Directory.GetFiles(dir, "*.txt").Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));
        return Catalog.FromHelpFiles("7.2.20", File.ReadAllText(Path.Combine(dir, "_usage.txt")), helps);
    });

    /// <summary>The subcommands of manual sections 8.5 to 8.55.</summary>
    private static readonly string[] ManualCommands =
    [
        "list", "showvminfo", "registervm", "unregistervm", "createvm", "modifyvm", "clonevm", "movevm", "encryptvm",
        "cloud", "cloudprofile", "import", "export", "signova", "startvm", "controlvm", "unattended", "discardstate",
        "adoptstate", "snapshot", "closemedium", "storageattach", "storagectl", "bandwidthctl", "showmediuminfo",
        "createmedium", "modifymedium", "clonemedium", "mediumproperty", "encryptmedium", "checkmediumpwd",
        "convertfromraw", "mediumio", "setextradata", "getextradata", "setproperty", "usbfilter", "sharedfolder",
        "guestproperty", "guestcontrol", "debugvm", "metrics", "natnetwork", "hostonlyif", "hostonlynet", "dhcpserver",
        "usbdevsource", "extpack", "updatecheck", "modifynvram", "vboximg-mount",
    ];

    /// <summary>Set VBW_WRITE_FALLBACK to a file path to regenerate the bundled descriptions from the newest fixture.</summary>
    [Fact]
    public void Bundled_descriptions_can_be_regenerated()
    {
        string? target = Environment.GetEnvironmentVariable("VBW_WRITE_FALLBACK");
        var data = Fallback.From(Installed.Value);
        Assert.True(data["modifyvm"].Options.Count > 150);
        if (!string.IsNullOrEmpty(target)) File.WriteAllText(target, System.Text.Json.JsonSerializer.Serialize(data));
    }

    [Fact]
    public void Every_manual_section_has_a_command_in_the_catalog()
    {
        foreach (var name in ManualCommands)
        {
            var doc = Installed.Value.Find(name);
            Assert.True(doc != null, name + " is missing");
            Assert.NotEmpty(doc!.Synopses);
        }
    }

    [Fact]
    public void Every_manual_command_has_a_room()
    {
        foreach (var name in ManualCommands)
            Assert.NotEqual(Room.More, Curation.RoomOf(name));
    }

    [Fact]
    public void Commands_newer_than_the_manual_still_appear()
    {
        var doc = Installed.Value.Find("objtracker");
        Assert.NotNull(doc);
        Assert.Equal(Room.More, Curation.RoomOf("objtracker"));
        var index = new SearchIndex(Installed.Value);
        Assert.Contains(index.Search("objtracker"), e => e.Doc.Name == "objtracker");
    }

    [Fact]
    public void Every_curated_phrase_resolves()
    {
        var index = new SearchIndex(Installed.Value);
        var curated = index.Entries.Where(e => e.Curated).Select(e => e.Title).ToHashSet();
        foreach (var p in Curation.Phrases) Assert.True(curated.Contains(p.Text), "Phrase has no target: " + p.Text + " -> " + p.Path);
        foreach (var e in index.Entries.Where(e => e.Curated && e.Presets != null))
            foreach (var key in e.Presets!.Keys)
                Assert.True(e.Synopsis.Options.Any(o => o.Name == key), $"'{e.Title}' presets unknown option {key}");
    }

    [Theory]
    [InlineData("forward port", "natpf")]
    [InlineData("snapshot", "snapshot take")]
    [InlineData("ram", "modifyvm")]
    [InlineData("resize disk", "modifymedium")]
    [InlineData("--nested-hw-virt", "modifyvm")]
    [InlineData("secure boot", "modifynvram")]
    public void Search_finds_plain_language_and_option_names(string query, string expectedPathPart)
    {
        var hits = new SearchIndex(Installed.Value).Search(query, 5);
        Assert.Contains(hits, h => h.Synopsis.Path.Contains(expectedPathPart) || h.Synopsis.Raw.Contains(expectedPathPart));
    }

    [Fact]
    public void Every_option_of_every_command_is_searchable()
    {
        var index = new SearchIndex(Installed.Value);
        int options = Installed.Value.Commands.Sum(c => c.Synopses.Sum(s => s.Options.Count()));
        Assert.Equal(options, index.Entries.Count(e => e.FocusOption != null && !e.Curated));
        Assert.True(options > 700, "expected several hundred options, got " + options);
    }

    [Fact]
    public void Long_summaries_are_read_across_lines()
    {
        Assert.Equal("Attach, remove, and modify storage media used by a virtual machine", Installed.Value.Find("storageattach")!.Summary);
        Assert.False(Installed.Value.Find("storageattach")!.Legacy);
    }

    [Fact]
    public void Titles_come_from_the_installed_help()
    {
        var doc = Installed.Value.Find("controlvm")!;
        Assert.Equal("Pause a Virtual Machine", doc.Titles["controlvm pause"]);
    }

    [Fact]
    public void Modifyvm_options_spread_over_zones_and_none_is_lost()
    {
        var opts = Installed.Value.Find("modifyvm")!.Synopses.SelectMany(s => s.Options).ToList();
        var byZone = opts.GroupBy(o => Curation.ZoneOfOption(o.Name)).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(opts.Count, byZone.Values.Sum());
        Assert.True(byZone[Zone.Board] > 30);
        Assert.True(byZone[Zone.Ports] > 40);
        Assert.Equal(Zone.Ports, Curation.ZoneOfOption("--nic"));
        Assert.Equal(Zone.Board, Curation.ZoneOfOption("--memory"));
        Assert.Equal(Zone.More, Curation.ZoneOfOption("--some-future-option"));
    }

    [Theory]
    [InlineData(true, "unregistervm", "vm", "--delete")]
    [InlineData(true, "controlvm", "vm", "poweroff")]
    [InlineData(true, "snapshot", "vm", "restore", "s1")]
    [InlineData(true, "modifynvram", "vm", "inituefivarstore")]
    [InlineData(false, "controlvm", "vm", "pause")]
    [InlineData(false, "snapshot", "vm", "take", "s1")]
    [InlineData(false, "modifyvm", "vm", "--memory=2048")]
    public void Dangerous_commands_are_flagged(bool expected, params string[] args) =>
        Assert.Equal(expected, Curation.IsDangerous(args));

    [Fact]
    public void Machine_readable_info_parses()
    {
        var info = VmInfo.Parse("""
            name="arch linux"
            UUID="39610e64-7865-49cd-95b7-5fd21b8a916a"
            memory=4096
            cpus=2
            VMState="poweroff"
            CfgFile="D:\\VirtualBox VMs\\arch\\arch.vbox"
            storagecontrollername0="SATA"
            storagecontrollertype0="IntelAhci"
            storagecontrollerportcount0="2"
            "SATA-0-0"="D:\\disks\\arch.vdi"
            "SATA-ImageUUID-0-0"="3a942625-4b9d-4cee-815b-19f006fd342a"
            "SATA-1-0"="none"
            nic1="intnet"
            intnet1="lab-net"
            nic2="none"
            SnapshotName="fresh"
            SnapshotUUID="aaa"
            SnapshotName-1="patched"
            SnapshotUUID-1="bbb"
            CurrentSnapshotUUID="bbb"
            """);
        Assert.Equal("arch linux", info.Name);
        Assert.Equal(4096, info.GetInt("memory"));
        Assert.True(info.CanModify);
        Assert.Equal(@"D:\VirtualBox VMs\arch\arch.vbox", info.Get("CfgFile"));
        var sata = Assert.Single(info.Controllers());
        Assert.Equal(2, sata.Slots.Count);
        Assert.Equal("arch.vdi", sata.Slots[0].Label);
        Assert.True(sata.Slots[1].Empty);
        Assert.Equal("lab-net", info.Nics()[0].Detail);
        var snaps = info.Snapshots();
        Assert.Equal(2, snaps.Count);
        Assert.True(snaps[1].Current);
        Assert.Equal(1, snaps[1].Depth);
    }

    [Fact]
    public void Vm_list_parses_names_with_spaces()
    {
        var list = VmInfo.ParseVmList("\"Windows 10\" {1bf3464d-57c6-4d49-92a9-a5cc3816b7e7}\n\"ol7\" {43349d78-2ab3-4cb8-978f-0e755cd98090}\n");
        Assert.Equal(2, list.Count);
        Assert.Equal("Windows 10", list[0].Name);
    }
}
