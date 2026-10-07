using System.Text.Json;
using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

/// <summary>
/// The command catalog for the installed VirtualBox. It is read from VBoxManage's own help output,
/// so commands and options added by newer versions appear without an app update.
/// </summary>
public sealed class Catalog
{
    /// <summary>Bump when the parser changes so stale caches are rebuilt.</summary>
    public const int FormatVersion = 5;

    public int Format { get; set; } = FormatVersion;
    public string Version { get; set; } = "";
    public List<CommandDoc> Commands { get; set; } = [];

    private static readonly HashSet<string> MetaCommands = new(StringComparer.OrdinalIgnoreCase) { "help", "commands" };

    /// <summary>A cached entry is only trusted when nothing the views rely on is missing.</summary>
    private static bool Usable(CommandDoc? d) =>
        d is { Name: not null, Synopses: not null, OptionHelp: not null, Titles: not null, FullText: not null, Summary: not null } &&
        d.Synopses.All(s => s is { Command: not null, Raw: not null, Elements: not null } &&
                            s.Elements.All(e => e is { Name: not null, Placeholder: not null, Choices: not null }));

    public CommandDoc? Find(string name) =>
        Commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Synopsis? FindSynopsis(string path, Func<Synopsis, bool>? filter = null) =>
        Commands.SelectMany(c => c.Synopses).FirstOrDefault(s => s.Path == path && (filter?.Invoke(s) ?? true));

    public static async Task<Catalog> LoadAsync(VBoxRunner runner, string cacheDir, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ver = await runner.RunAsync(["--version"], ct);
        string version = ver.Output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "unknown";
        string safe = Regex.Replace(version, @"[^A-Za-z0-9._-]", "_");
        string cacheFile = Path.Combine(cacheDir, $"catalog-{safe}.json");

        if (File.Exists(cacheFile))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<Catalog>(await File.ReadAllTextAsync(cacheFile, ct));
                if (cached is { Format: FormatVersion, Commands.Count: > 0 } && cached.Commands.All(Usable)) return cached;
            }
            // A cache that is damaged, half-written or unreadable is simply rebuilt.
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { }
        }

        progress?.Report("Reading the command list from VirtualBox " + version);
        var catalog = await BuildAsync(runner, version, progress, ct);
        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllTextAsync(cacheFile, JsonSerializer.Serialize(catalog), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        return catalog;
    }

    public static async Task<Catalog> BuildAsync(VBoxRunner runner, string version, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var dumpRun = await runner.RunAsync([], ct);
        var dump = ParseAnyUsage(dumpRun.Combined);

        var names = new List<string>(dump.Keys.Where(k => !MetaCommands.Contains(k)));
        // Older versions answer an unknown command with the whole usage text, so only a clean exit counts.
        var listed = await runner.RunAsync(["commands"], ct);
        if (listed.Ok)
            foreach (var w in listed.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                if (Regex.IsMatch(w, "^[a-z][a-z0-9-]+$") && !MetaCommands.Contains(w) && !names.Contains(w, StringComparer.OrdinalIgnoreCase))
                    names.Add(w);

        // "VBoxManage help <command>" exists since 7.0. Ask once before asking fifty times.
        bool hasHelp = false;
        if (names.Count > 0)
        {
            // The exit code says nothing here: 7.2 returns 1 from "help" even when it prints the page.
            var probe = await runner.RunAsync(["help", names[0]], ct);
            hasHelp = HelpParser.TryParseCommandHelp(names[0], probe.Output).Synopses.Count > 0;
        }

        var docs = new CommandDoc[names.Count];
        using var gate = new SemaphoreSlim(6);
        int done = 0;
        await Task.WhenAll(names.Select(async (name, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var doc = new CommandDoc { Name = name };
                if (hasHelp)
                {
                    var help = await runner.RunAsync(["help", name], ct);
                    doc = HelpParser.TryParseCommandHelp(name, help.Output);
                    progress?.Report($"Reading help: {Interlocked.Increment(ref done)} of {names.Count}");
                }
                // Some commands (hostonlynet in 7.2) are only described in the all-commands usage dump,
                // and before 7.0 that dump is all there is.
                if (doc.Synopses.Count == 0 && dump.TryGetValue(name, out var fromDump)) doc = FromUsageOnly(name, fromDump);
                docs[i] = doc;
            }
            finally { gate.Release(); }
        }));

        var catalog = new Catalog { Version = version };
        catalog.Commands.AddRange(docs.Where(d => d.Synopses.Count > 0));
        catalog.Commands.Add(Extras.VBoxImgMount());
        return catalog;
    }

    /// <summary>Reads a usage dump in either style: one "VBoxManage ..." line per action (7.x) or command blocks (6.x and older).</summary>
    public static Dictionary<string, List<Synopsis>> ParseAnyUsage(string text)
    {
        var modern = HelpParser.ParseUsageDump(text);
        if (!LegacyUsage.LooksLegacy(text)) return modern;
        // 6.1 mixes both: the old blocks first, then newer-style lines for the commands that had been converted.
        foreach (var (name, synopses) in LegacyUsage.Parse(text))
            if (!modern.ContainsKey(name)) modern[name] = synopses;
        return modern;
    }

    /// <summary>A command known only from its usage lines. Summary and titles are borrowed from the bundled text.</summary>
    public static CommandDoc FromUsageOnly(string name, List<Synopsis> synopses)
    {
        var bundled = Fallback.For(name);
        return new CommandDoc
        {
            Name = name, Synopses = synopses, Legacy = true,
            Summary = bundled?.Summary ?? "",
            Titles = bundled != null ? new(bundled.Titles) : [],
            FullText = string.Join("\n\n", synopses.Select(s => s.Raw)) +
                       "\n\nThis VirtualBox version prints only the syntax shown above. The explanations in the forms come from a newer " +
                       "VirtualBox manual and can differ in detail from what this version does.",
        };
    }

    /// <summary>Builds a catalog from saved help files. Used by tests and as an offline fallback.</summary>
    public static Catalog FromHelpFiles(string version, string usageDump, IEnumerable<(string Name, string Text)> helps)
    {
        var dump = ParseAnyUsage(usageDump);
        var catalog = new Catalog { Version = version };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in helps)
        {
            if (MetaCommands.Contains(name)) continue;
            var doc = HelpParser.ParseCommandHelp(name, text);
            if (doc.Synopses.Count > 0 && seen.Add(name)) catalog.Commands.Add(doc);
        }
        foreach (var (name, synopses) in dump)
        {
            if (MetaCommands.Contains(name) || !seen.Add(name)) continue;
            catalog.Commands.Add(FromUsageOnly(name, synopses));
        }
        catalog.Commands.Add(Extras.VBoxImgMount());
        return catalog;
    }
}

/// <summary>Tools from the manual chapter that are separate programs rather than VBoxManage subcommands.</summary>
public static class Extras
{
    /// <summary>Section 8.55. It ships only on Linux and macOS hosts, so elsewhere the command can be built but not run.</summary>
    public static CommandDoc VBoxImgMount()
    {
        string[] usage =
        [
            "VBoxManage vboximg-mount <--image=image-UUID> [--guest-filesystem] [-o=FUSE-option[,FUSE-option]] [--root] [--rw] <mountpoint>",
            "VBoxManage vboximg-mount <--list> [--image=image-UUID] [--guest-filesystem] [--verbose] [--vm=vm-UUID] [--wide]",
            "VBoxManage vboximg-mount <--help>",
        ];
        var doc = new CommandDoc
        {
            Name = "vboximg-mount",
            Program = "vboximg-mount",
            Runnable = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            Summary = "FUSE mount a virtual disk image (Linux and macOS hosts only)",
            Synopses = usage.Select(SynopsisParser.Parse).ToList(),
            FullText = "vboximg-mount makes VirtualBox disk images available to a macOS or Linux host through FUSE. " +
                       "You can mount any version of a disk from its snapshot history, view its contents and optionally modify them.\n\n" +
                       "It is a separate program, not a VBoxManage subcommand, and it is not shipped on Windows hosts.",
            OptionHelp =
            {
                ["--image"] = "The UUID, name or path of the VirtualBox disk image. Short form: -i.",
                ["--guest-filesystem"] = "Enables experimental read-only support for guest file systems (FAT, NTFS, ext2, ext3, ext4). Short form: -g.",
                ["-o"] = "FUSE mount options, as described in the mount.fuse(8) man page.",
                ["--root"] = "Also grants file access to the root user. Same as -o allow_root. Incompatible with -o allow_other.",
                ["--rw"] = "Mounts the image read-write. By default images are mounted read-only.",
                ["--list"] = "Shows the disks of registered VMs, or the partitions of the given image. Short form: -l.",
                ["--verbose"] = "Shows detailed information, including snapshot images and file paths. Short form: -v.",
                ["--vm"] = "Shows information about the VM with this UUID.",
                ["--wide"] = "Wide output, including the lock state of running VMs and the snapshot tree.",
                ["--help"] = "Shows usage information.",
            },
            Titles =
            {
                ["vboximg-mount"] = "Mount or list a disk image",
            },
        };
        foreach (var s in doc.Synopses) s.Raw = s.Raw.Replace("VBoxManage vboximg-mount", "vboximg-mount");
        return doc;
    }
}
