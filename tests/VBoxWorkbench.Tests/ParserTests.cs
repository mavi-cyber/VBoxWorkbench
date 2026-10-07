using System.Text;
using VBoxWorkbench.Core;
using Xunit;

namespace VBoxWorkbench.Tests;

public class ParserTests
{
    private static string Fixture(params string[] parts) =>
        Path.Combine(new[] { AppContext.BaseDirectory, "Fixtures" }.Concat(parts).ToArray());

    private static CommandDoc Doc(string name) =>
        HelpParser.ParseCommandHelp(name, File.ReadAllText(Fixture("v7.2.20", name + ".txt")));

    private static string Dump(Synopsis s)
    {
        var sb = new StringBuilder(s.Path + " ::");
        foreach (var e in s.Elements)
        {
            sb.Append(' ').Append(e.Kind switch { ElementKind.Keyword => "K", ElementKind.Option => "O", ElementKind.Positional => "P", _ => "T" });
            sb.Append(e.Required ? "!" : "?").Append('(').Append(e.Name);
            if (e.Indexed) sb.Append("#");
            if (e.HasValue && e.Kind == ElementKind.Option) sb.Append(e.UsesEquals ? "=" : " ").Append(e.Placeholder);
            if (e.Choices.Count > 0) sb.Append(" {").Append(string.Join(',', e.Choices)).Append('}');
            if (e.Repeatable) sb.Append("...");
            if (e.SplitValue) sb.Append(" split");
            if (e.ExclusiveGroup >= 0) sb.Append(" x").Append(e.ExclusiveGroup);
            sb.Append(')');
        }
        return sb.ToString();
    }

    [Fact]
    public void Every_installed_command_parses_with_at_least_one_synopsis()
    {
        var sb = new StringBuilder();
        foreach (var file in Directory.GetFiles(Fixture("v7.2.20"), "*.txt").Where(f => !Path.GetFileName(f).StartsWith('_')))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (name is "internalcommands") continue;
            var doc = HelpParser.ParseCommandHelp(name, File.ReadAllText(file));
            Assert.True(doc.Synopses.Count > 0, name + " has no synopsis");
            Assert.False(string.IsNullOrWhiteSpace(doc.Summary), name + " has no summary");
            foreach (var s in doc.Synopses) sb.AppendLine(Dump(s));
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dump-7.2.20.txt"), sb.ToString());
    }

    [Fact]
    public void Usage_dump_covers_commands_missing_from_help()
    {
        var all = HelpParser.ParseUsageDump(File.ReadAllText(Fixture("v7.2.20", "_usage.txt")));
        Assert.True(all.ContainsKey("hostonlynet"));
        Assert.Equal(3, all["hostonlynet"].Count);
        Assert.True(all.Count >= 50);
    }

    [Fact]
    public void Manual_7_0_synopses_parse_too()
    {
        var all = HelpParser.ParseUsageDump(File.ReadAllText(Fixture("manual-7.0", "synopses.txt")));
        var sb = new StringBuilder();
        foreach (var s in all.Values.SelectMany(v => v)) sb.AppendLine(Dump(s));
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "dump-7.0.txt"), sb.ToString());
        foreach (var c in new[] { "list", "modifyvm", "controlvm", "snapshot", "storageattach", "dhcpserver", "guestcontrol", "modifynvram", "cloud", "mediumio" })
            Assert.True(all.ContainsKey(c), c);
        var vm = all["startvm"][0].Elements.First(e => e.Kind == ElementKind.Positional);
        Assert.True(vm.IsVm);
    }

    [Fact]
    public void Snapshot_take_has_keyword_name_and_options()
    {
        var take = Doc("snapshot").Synopses.Single(s => s.Path == "snapshot take");
        Assert.True(take.Elements[0].IsVm);
        Assert.Equal(ElementKind.Keyword, take.Elements[1].Kind);
        Assert.Equal("snapshot-name", take.Elements[2].Placeholder);
        Assert.True(take.Elements[2].Required);
        Assert.Contains(take.Options, o => o.Name == "--live" && !o.HasValue);
        Assert.Contains(take.Options, o => o.Name == "--description" && o.HasValue && o.UsesEquals);
        Assert.Contains(take.Options, o => o.Name == "--uniquename" && o.HasValue && !o.UsesEquals);
    }

    [Fact]
    public void Modifyvm_options_have_choices_indexes_and_help()
    {
        var doc = Doc("modifyvm");
        var opts = doc.Synopses.SelectMany(s => s.Options).ToList();
        var nic = opts.First(o => o.Name == "--nic");
        Assert.True(nic.Indexed);
        Assert.Contains("bridged", nic.Choices);
        var fw = opts.First(o => o.Name == "--firmware");
        Assert.Equal(["bios", "efi", "efi32", "efi64"], fw.Choices.Take(4));
        Assert.Contains(opts, o => o.Name == "--memory" && o.HasValue && o.Choices.Count == 0);
        Assert.Contains(opts, o => o.Name == "--nat-pf" && o.Indexed && o.Choices.Count == 0);
        Assert.Contains("RAM", doc.OptionHelp["--memory"]);
        Assert.True(doc.OptionHelp.ContainsKey("--nic-type"));
        Assert.True(opts.Count > 150);
    }

    [Fact]
    public void Exclusive_alternatives_share_a_group()
    {
        var list = Doc("snapshot").Synopses.Single(s => s.Path == "snapshot list");
        var a = list.Options.Single(o => o.Name == "--details");
        var b = list.Options.Single(o => o.Name == "--machinereadable");
        Assert.True(a.ExclusiveGroup >= 0);
        Assert.Equal(a.ExclusiveGroup, b.ExclusiveGroup);
    }

    [Fact]
    public void Builder_emits_arguments_in_synopsis_order()
    {
        var take = Doc("snapshot").Synopses.Single(s => s.Path == "snapshot take");
        var values = take.Elements.Select(e => e switch
        {
            { IsVm: true } => new ElementValue(e, "my vm"),
            { Kind: ElementKind.Keyword } => new ElementValue(e, ""),
            { Kind: ElementKind.Positional } => new ElementValue(e, "before update"),
            { Name: "--live" } => new ElementValue(e, ""),
            { Name: "--description" } => new ElementValue(e, "hello there"),
            _ => new ElementValue(e, "", Enabled: false),
        });
        var args = CommandLine.Build(take, values);
        Assert.Equal(["snapshot", "my vm", "take", "before update", "--description=hello there", "--live"], args);
        Assert.Equal("VBoxManage snapshot \"my vm\" take \"before update\" \"--description=hello there\" --live",
            new BuiltCommand { Args = args }.Display);
    }

    [Fact]
    public void Indexed_keyword_and_split_values_build_correctly()
    {
        var doc = Doc("controlvm");
        var natpf = doc.Synopses.First(s => s.Path == "controlvm natpf" && !s.Raw.Contains("delete"));
        var kw = natpf.Elements.First(e => e.Kind == ElementKind.Keyword);
        Assert.True(kw.Indexed);
        var args = CommandLine.Build(natpf, natpf.Elements.Select(e => e.Kind switch
        {
            ElementKind.Keyword => new ElementValue(e, "", "1"),
            _ => e.IsVm ? new ElementValue(e, "ol7") : new ElementValue(e, "ssh,tcp,,2222,,22"),
        }));
        Assert.Equal(["controlvm", "ol7", "natpf1", "ssh,tcp,,2222,,22"], args);

        var uart = doc.Synopses.First(s => s.Path.StartsWith("controlvm changeuartmode"));
        var uargs = CommandLine.Build(uart, uart.Elements.Select(e => e.Kind switch
        {
            ElementKind.Keyword => new ElementValue(e, "", "1"),
            _ => e.IsVm ? new ElementValue(e, "ol7") : new ElementValue(e, "tcpserver 5000"),
        }));
        Assert.Equal(["controlvm", "ol7", "changeuartmode1", "tcpserver", "5000"], uargs);
    }

    [Fact]
    public void Guestcontrol_run_has_passthrough()
    {
        var run = Doc("guestcontrol").Synopses.First(s => s.Path == "guestcontrol run");
        Assert.Contains(run.Elements, e => e.Kind == ElementKind.Passthrough);
        Assert.Contains(run.Options, o => o.Name == "--exe");
    }
}
