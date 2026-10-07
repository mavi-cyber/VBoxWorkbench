using System.Text;
using System.Text.Json;
using VBoxWorkbench.Core;
using Xunit;

namespace VBoxWorkbench.Tests;

/// <summary>
/// The app reads text it does not control: usage lines from whatever VirtualBox is installed, machine info,
/// and its own cache file. None of it may be able to take the app down.
/// </summary>
public class RobustnessTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private static List<string> RealUsageLines()
    {
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(Fixtures, "v7.2.20"), "*.txt"))
            lines.AddRange(HelpParser.SplitSynopses(File.ReadAllText(file).Replace("\r", "").Split('\n')));
        lines.AddRange(HelpParser.SplitSynopses(File.ReadAllText(Path.Combine(Fixtures, "manual-7.0", "synopses.txt")).Split('\n')));
        return lines.Distinct().ToList();
    }

    private static string Mutate(string line, Random rng)
    {
        const string noise = "[]<>|=,.- N\"'()#@:/\\";
        var sb = new StringBuilder(line);
        int edits = rng.Next(1, 6);
        for (int i = 0; i < edits && sb.Length > 0; i++)
        {
            int at = rng.Next(sb.Length);
            switch (rng.Next(5))
            {
                case 0: sb.Remove(at, Math.Min(rng.Next(1, 8), sb.Length - at)); break;
                case 1: sb.Insert(at, noise[rng.Next(noise.Length)]); break;
                case 2: sb[at] = noise[rng.Next(noise.Length)]; break;
                case 3: sb.Insert(at, sb.ToString(rng.Next(sb.Length), Math.Min(12, sb.Length - rng.Next(sb.Length) > 0 ? 1 : 0))); break;
                default: sb.Length = at; break; // cut the line short
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void Usage_parser_survives_damaged_and_unfamiliar_lines()
    {
        var real = RealUsageLines();
        Assert.True(real.Count > 300);
        var rng = new Random(20261008);
        var failures = new Dictionary<string, string>();
        for (int i = 0; i < 60_000; i++)
        {
            string line = Mutate(real[rng.Next(real.Count)], rng);
            try
            {
                var syn = SynopsisParser.Parse(line);
                // Whatever came out must also survive being turned into a command line.
                CommandLine.Build(syn, syn.Elements.Select(e => new ElementValue(e, "a \"b c\" d", "1")));
            }
            catch (Exception ex)
            {
                failures.TryAdd(ex.GetType().Name + " at " + ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim(), line);
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(5).Select(f => f.Key + "\n   " + f.Value)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("VBoxManage")]
    [InlineData("VBoxManage [")]
    [InlineData("VBoxManage ]]]>>> | | |")]
    [InlineData("VBoxManage x <")]
    [InlineData("VBoxManage x --=")]
    [InlineData("VBoxManage x -- ")]
    [InlineData("VBoxManage x [--a=|] <|> [|]")]
    [InlineData("VBoxManage x N")]
    [InlineData("VBoxManage x --N= a | b")]
    [InlineData("| | |")]
    public void Degenerate_usage_lines_parse_without_error(string line)
    {
        var syn = SynopsisParser.Parse(line);
        CommandLine.Build(syn, syn.Elements.Select(e => new ElementValue(e, "", "")));
    }

    [Fact]
    public void Help_and_legacy_readers_survive_damaged_text()
    {
        var rng = new Random(7);
        var texts = Directory.GetFiles(Path.Combine(Fixtures, "v7.2.20"), "*.txt").Select(File.ReadAllText)
            .Append(File.ReadAllText(Path.Combine(Fixtures, "legacy-6.1", "usage.txt"))).ToList();
        for (int i = 0; i < 400; i++)
        {
            string text = texts[rng.Next(texts.Count)];
            // Cut a slice out of the middle and shuffle a few lines, as a truncated or reformatted help page would look.
            int a = rng.Next(text.Length), b = Math.Min(text.Length, a + rng.Next(2000));
            string damaged = text[..a] + text[b..];
            var lines = damaged.Split('\n').ToList();
            for (int k = 0; k < 5 && lines.Count > 2; k++) lines.RemoveAt(rng.Next(lines.Count));
            damaged = string.Join('\n', lines);

            var doc = HelpParser.ParseCommandHelp("modifyvm", damaged);
            Assert.NotNull(doc.Synopses);
            HelpParser.ParseUsageDump(damaged);
            LegacyUsage.Parse(damaged);
            Catalog.ParseAnyUsage(damaged);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("=")]
    [InlineData("\"\"=\"\"")]
    [InlineData("name=")]
    [InlineData("storagecontrollername0=\"(weird[name\"\n\"(weird[name-0-0\"=\"none\"\n\"(weird[name-x-y\"=\"none\"")]
    [InlineData("nic1=\nnic2\nSnapshotName=\"a\"\nSnapshotName-1-1-1-1=\"b\"\nCurrentSnapshotUUID=")]
    [InlineData("storagecontrollername0=\"SATA\"\n\"SATA-99999999999999999999-0\"=\"x\"")]
    public void Odd_machine_info_is_read_without_error(string text)
    {
        var info = VmInfo.Parse(text);
        _ = info.Controllers();
        _ = info.Nics();
        _ = info.Snapshots();
        _ = info.CanModify;
        VmInfo.ParseVmList(text);
        VmInfo.ParseBlocks(text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Format\":5,\"Version\":\"x\",\"Commands\":[null]}")]
    [InlineData("{\"Format\":5,\"Version\":\"x\",\"Commands\":[{\"Name\":\"list\",\"Synopses\":null}]}")]
    [InlineData("{\"Format\":5,\"Version\":\"x\",\"Commands\":[{\"Name\":\"list\",\"Synopses\":[{\"Command\":\"list\",\"Raw\":\"\",\"Elements\":[null]}]}]}")]
    public async Task A_damaged_cache_file_is_rebuilt_not_trusted(string content)
    {
        string? exe = VBoxLocator.Find();
        if (exe == null) return;
        var runner = new VBoxRunner(exe);
        string dir = Path.Combine(Path.GetTempPath(), "vbw-cache-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string version = (await runner.RunAsync(["--version"])).Output.Trim().Split('\n').Last().Trim();
            string safe = System.Text.RegularExpressions.Regex.Replace(version, @"[^A-Za-z0-9._-]", "_");
            await File.WriteAllTextAsync(Path.Combine(dir, $"catalog-{safe}.json"), content.Replace("\"Format\":5", "\"Format\":" + Catalog.FormatVersion));
            var catalog = await Catalog.LoadAsync(runner, dir);
            Assert.True(catalog.Commands.Count >= 50);
            Assert.All(catalog.Commands, c => Assert.NotEmpty(c.Synopses));
            // The index and every title must be buildable from what was loaded.
            var index = new SearchIndex(catalog);
            Assert.NotEmpty(index.Search("snapshot"));
            // And the rebuilt cache round-trips.
            var again = JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(Path.Combine(dir, $"catalog-{safe}.json")));
            Assert.Equal(catalog.Commands.Count, again!.Commands.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Search_handles_any_query()
    {
        string dir = Path.Combine(Fixtures, "v7.2.20");
        var helps = Directory.GetFiles(dir, "*.txt").Where(f => !Path.GetFileName(f).StartsWith('_'))
            .Select(f => (Path.GetFileNameWithoutExtension(f), File.ReadAllText(f)));
        var index = new SearchIndex(Catalog.FromHelpFiles("7.2.20", File.ReadAllText(Path.Combine(dir, "_usage.txt")), helps));
        foreach (var q in new[] { "", " ", "   \t ", "--", "((((", "\\", "*?[", new string('x', 5000), "ａｂｃ", "nic nic nic nic", "\0" })
            _ = index.Search(q);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\"")]
    [InlineData("\"\"\"")]
    [InlineData("a \"b")]
    [InlineData("   ")]
    public void Argument_splitting_handles_unbalanced_quotes(string text) => _ = CommandLine.Split(text);
}
