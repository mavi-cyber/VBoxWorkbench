using System.Reflection;
using System.Text.Json;

namespace VBoxWorkbench.Core;

/// <summary>
/// Compares option names across VirtualBox versions. 6.x wrote --nictype and --natpf where 7.x writes
/// --nic-type and --nat-pf, and 7.2 added an x86 prefix to some (--x86-long-mode).
/// </summary>
public static class OptionNames
{
    private static readonly Dictionary<string, string> Alias = new()
    {
        ["clipboardmode"] = "clipboard",
        ["videocap"] = "recording", ["videocapfile"] = "recordingfile", ["videocapscreens"] = "recordingscreens",
        ["videocapres"] = "recordingvideores", ["videocaprate"] = "recordingvideorate", ["videocapfps"] = "recordingvideofps",
        ["videocapmaxtime"] = "recordingmaxtime", ["videocapmaxsize"] = "recordingmaxsize", ["videocapopts"] = "recordingopts",
    };

    /// <summary>The name without dashes, prefix or old spelling: "--nic-type" and "--nictype" both give "nictype".</summary>
    public static string Canonical(string name)
    {
        string s = name.TrimStart('-').Replace("-", "").ToLowerInvariant();
        if (s.StartsWith("x86") && s.Length > 3) s = s[3..];
        return Alias.GetValueOrDefault(s, s);
    }

    public static bool Same(string a, string b) => a == b || Canonical(a) == Canonical(b);

    /// <summary>The option of this synopsis that means the same as <paramref name="wanted"/>, whatever it is called here.</summary>
    public static SynopsisElement? Find(Synopsis synopsis, string wanted) =>
        synopsis.Options.FirstOrDefault(o => o.Name == wanted) ?? synopsis.Options.FirstOrDefault(o => Same(o.Name, wanted));

    /// <summary>Renames preset keys to the names this synopsis uses and drops the ones it does not have.</summary>
    public static Dictionary<string, string>? Remap(Synopsis synopsis, Dictionary<string, string>? presets)
    {
        if (presets == null) return null;
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in presets)
            if (Find(synopsis, key) is { } o) result[o.Name] = value;
        return result;
    }
}

public sealed class FallbackCommand
{
    public string Summary { get; set; } = "";
    /// <summary>Canonical option name to description.</summary>
    public Dictionary<string, string> Options { get; set; } = [];
    public Dictionary<string, string> Titles { get; set; } = [];
}

/// <summary>
/// Descriptions bundled with the app, taken from a recent VirtualBox. Older versions print usage lines
/// without any explanation, so the forms borrow the text from here. The syntax always comes from the
/// installed version; only the explanatory text is borrowed.
/// </summary>
public static class Fallback
{
    private static readonly Lazy<Dictionary<string, FallbackCommand>> Data = new(Load);

    private static Dictionary<string, FallbackCommand> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VBoxWorkbench.Core.fallback-help.json");
        if (stream == null) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, FallbackCommand>>(stream) ?? []; }
        catch (JsonException) { return []; }
    }

    public static FallbackCommand? For(string command) => Data.Value.GetValueOrDefault(command);

    public static string OptionHelp(string command, string option) =>
        For(command)?.Options.GetValueOrDefault(OptionNames.Canonical(option)) ?? "";

    /// <summary>Builds the bundled data from a catalog. Used once, by a test, to refresh fallback-help.json.</summary>
    public static Dictionary<string, FallbackCommand> From(Catalog catalog)
    {
        var data = new Dictionary<string, FallbackCommand>();
        foreach (var doc in catalog.Commands)
        {
            var entry = new FallbackCommand { Summary = doc.Summary, Titles = new(doc.Titles) };
            foreach (var (name, help) in doc.OptionHelp)
            {
                string key = OptionNames.Canonical(name);
                if (!entry.Options.ContainsKey(key)) entry.Options[key] = help.Length > 500 ? help[..500].TrimEnd() + "..." : help;
            }
            data[doc.Name] = entry;
        }
        return data;
    }
}
