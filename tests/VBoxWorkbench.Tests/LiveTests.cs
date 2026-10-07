using VBoxWorkbench.Core;
using Xunit;

namespace VBoxWorkbench.Tests;

/// <summary>
/// Runs against the VirtualBox installed on this computer. Everything happens on a throwaway VM in a temp
/// folder that is unregistered and deleted at the end; existing machines are only listed, never changed.
/// The tests do nothing when VirtualBox is not installed.
/// </summary>
public class LiveTests
{
    private static readonly string? Exe = VBoxLocator.Find();

    private static List<string> Build(Catalog catalog, string path, string vm, Dictionary<string, string> options,
        List<string>? positionals = null, string index = "1", Func<Synopsis, bool>? filter = null)
    {
        var syn = catalog.FindSynopsis(path, filter) ?? throw new InvalidOperationException("no synopsis " + path);
        int p = 0;
        var used = new HashSet<string>();
        var values = syn.Elements.Select(e => e.Kind switch
        {
            ElementKind.Keyword => new ElementValue(e, "", index),
            ElementKind.Positional when e.IsVm => new ElementValue(e, vm),
            ElementKind.Positional => new ElementValue(e, positionals != null && p < positionals.Count ? positionals[p++] : "",
                Enabled: positionals != null && p <= positionals.Count),
            ElementKind.Option => options.TryGetValue(e.Name, out var v) && used.Add(e.Name) ? new ElementValue(e, v, index) : new ElementValue(e, "", Enabled: false),
            _ => new ElementValue(e, "", Enabled: false),
        }).ToList();
        return CommandLine.Build(syn, values);
    }

    [Fact]
    public async Task Installed_version_yields_a_complete_catalog()
    {
        if (Exe == null) return;
        var catalog = await Catalog.BuildAsync(new VBoxRunner(Exe), "live");
        foreach (var name in new[] { "list", "modifyvm", "controlvm", "snapshot", "storageattach", "hostonlynet", "dhcpserver", "guestcontrol", "modifynvram", "vboximg-mount" })
            Assert.True(catalog.Find(name) is { Synopses.Count: > 0 }, name);
        Assert.True(catalog.Commands.Count >= 50);

        // On 7.0 and newer the descriptions must come from the installed version, not from the bundled text.
        var version = await new VBoxRunner(Exe).RunAsync(["--version"]);
        if (int.TryParse(version.Output.Trim().Split('.')[0], out int major) && major >= 7)
        {
            var modify = catalog.Find("modifyvm")!;
            Assert.False(modify.Legacy);
            Assert.True(modify.OptionHelp.Count > 100);
            Assert.DoesNotContain(catalog.Commands, c => c.Legacy && c.Name is "snapshot" or "storageattach" or "controlvm");
        }
    }

    [Fact]
    public async Task Generated_commands_are_accepted_by_the_real_VBoxManage()
    {
        if (Exe == null) return;
        var runner = new VBoxRunner(Exe);
        var catalog = await Catalog.BuildAsync(runner, "live");
        string vm = "vbw-selftest-" + Guid.NewGuid().ToString("N")[..8];
        string folder = Path.Combine(Path.GetTempPath(), vm);
        Directory.CreateDirectory(folder);

        async Task Ok(List<string> args)
        {
            var r = await runner.RunAsync(args);
            Assert.True(r.Ok, "VBoxManage " + string.Join(' ', args) + "\n" + r.Combined);
        }

        try
        {
            var create = new Dictionary<string, string> { ["--name"] = vm, ["--basefolder"] = folder, ["--register"] = "", ["--ostype"] = "Linux_64" };
            if (catalog.FindSynopsis("createvm")!.Options.Any(o => o.Name == "--platform-architecture")) create["--platform-architecture"] = "x86";
            await Ok(Build(catalog, "createvm", vm, create));

            await Ok(Build(catalog, "modifyvm", vm, new() { ["--memory"] = "768", ["--cpus"] = "2", ["--description"] = "made by a test, safe to delete" },
                filter: s => s.Options.Any(o => o.Name == "--memory")));
            await Ok(Build(catalog, "modifyvm", vm, new() { ["--nic"] = "intnet", ["--intnet"] = "vbw test net" }, index: "2",
                filter: s => s.Options.Any(o => o.Name == "--nic")));
            await Ok(Build(catalog, "modifyvm", vm, new() { ["--nat-pf"] = "ssh,tcp,,2222,,22" },
                filter: s => s.Options.Any(o => o.Name == "--nat-pf")));
            await Ok(Build(catalog, "storagectl", vm, new() { ["--name"] = "SATA", ["--add"] = "sata", ["--controller"] = "IntelAhci" }));

            string disk = Path.Combine(folder, "disk 1.vdi");
            await Ok(Build(catalog, "createmedium", vm, new() { ["--filename"] = disk, ["--size"] = "16" }, positionals: ["disk"]));
            await Ok(Build(catalog, "storageattach", vm, new() { ["--storagectl"] = "SATA", ["--port"] = "0", ["--device"] = "0", ["--type"] = "hdd", ["--medium"] = disk }));
            await Ok(Build(catalog, "snapshot take", vm, new() { ["--description"] = "first one" }, positionals: ["snap one"]));
            await Ok(Build(catalog, "sharedfolder add", vm, new() { ["--name"] = "share", ["--hostpath"] = folder, ["--automount"] = "" }));
            await Ok(Build(catalog, "setextradata", vm, [], positionals: ["vbw/test", "hello"]));

            var info = VmInfo.Parse((await runner.RunAsync(["showvminfo", vm, "--machinereadable"])).Output);
            Assert.Equal(vm, info.Name);
            Assert.Equal(768, info.GetInt("memory"));
            Assert.Equal(2, info.GetInt("cpus"));
            Assert.True(info.CanModify);
            Assert.Equal("intnet", info.Nics()[1].Mode);
            Assert.Equal("vbw test net", info.Nics()[1].Detail);
            var sata = info.Controllers().Single(c => c.Name == "SATA");
            Assert.Contains(sata.Slots, s => s.Label.EndsWith(".vdi"));
            Assert.Equal("snap one", Assert.Single(info.Snapshots()).Name);

            var listed = VmInfo.ParseVmList((await runner.RunAsync(["list", "vms"])).Output);
            Assert.Contains(listed, v => v.Name == vm);
            var disks = VmInfo.ParseBlocks((await runner.RunAsync(["list", "hdds"])).Output);
            Assert.Contains(disks, d => d.GetValueOrDefault("Location") == disk);
        }
        finally
        {
            await runner.RunAsync(["unregistervm", vm, "--delete"]);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var after = VmInfo.ParseVmList((await runner.RunAsync(["list", "vms"])).Output);
        Assert.DoesNotContain(after, v => v.Name == vm);
    }
}
