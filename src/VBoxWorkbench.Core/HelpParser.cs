using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

/// <summary>Everything the installed VBoxManage says about one subcommand.</summary>
public sealed class CommandDoc
{
    public string Name { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<Synopsis> Synopses { get; set; } = [];
    /// <summary>Option name (without index marker) to its description text.</summary>
    public Dictionary<string, string> OptionHelp { get; set; } = [];
    /// <summary>Synopsis path ("controlvm pause") to the heading the help gives it ("Pause a Virtual Machine").</summary>
    public Dictionary<string, string> Titles { get; set; } = [];
    /// <summary>False for tools that cannot be run on this host (vboximg-mount on Windows).</summary>
    public bool Runnable { get; set; } = true;
    /// <summary>Program to run when it is not VBoxManage itself.</summary>
    public string? Program { get; set; }
    /// <summary>The full help text, shown as-is in the reference pane.</summary>
    public string FullText { get; set; } = "";

    /// <summary>True when the installed version gave no descriptions and the bundled ones are shown instead.</summary>
    public bool Legacy { get; set; }

    public string HelpFor(SynopsisElement e)
    {
        if (e.Kind != ElementKind.Option) return "";
        if (OptionHelp.TryGetValue(e.Name, out var own)) return own;
        foreach (var (name, text) in OptionHelp)
            if (OptionNames.Same(name, e.Name)) return text;
        return Fallback.OptionHelp(Name, e.Name);
    }
}

/// <summary>Parses the text printed by "VBoxManage help &lt;command&gt;" and by bare "VBoxManage".</summary>
public static partial class HelpParser
{
    [GeneratedRegex(@"^\s*VBoxManage(\.exe)?\s+(\S+)\s+--\s+(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SummaryLine();

    [GeneratedRegex(@"^\s*VBoxManage(\.exe)?(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UsageStart();

    [GeneratedRegex(@"^(\s+)(--[A-Za-z0-9][A-Za-z0-9<>-]*)")]
    private static partial Regex OptionEntry();

    [GeneratedRegex(@"^\s*[=\-~]{3,}\s*$")]
    private static partial Regex Underline();

    public static CommandDoc ParseCommandHelp(string name, string text)
    {
        var doc = new CommandDoc { Name = name, FullText = text.Replace("\r", "") };
        var lines = doc.FullText.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var m = SummaryLine().Match(lines[i]);
            if (!m.Success) continue;
            // The summary wraps onto the next lines when it is long.
            string summary = m.Groups[3].Value.Trim();
            for (int j = i + 1; j < lines.Length && lines[j].Trim().Length > 0; j++) summary += " " + lines[j].Trim();
            doc.Summary = summary.TrimEnd('.');
            break;
        }

        int usage = Array.FindIndex(lines, l => l.Trim() == "Usage");
        int desc = Array.FindIndex(lines, l => l.Trim() == "Description");
        if (usage >= 0)
        {
            int end = desc > usage ? desc : lines.Length;
            foreach (var raw in SplitSynopses(lines.Skip(usage + 1).Take(end - usage - 1)))
            {
                var syn = SynopsisParser.Parse(raw);
                if (syn.Command.Equals(name, StringComparison.OrdinalIgnoreCase)) doc.Synopses.Add(syn);
            }
        }
        ParseOptionHelp(lines, Math.Max(desc, 0), doc.OptionHelp);
        ParseTitles(lines, Math.Max(desc, 0), name, doc.Titles);
        return doc;
    }

    /// <summary>Parses the all-commands usage dump into synopses grouped by command name.</summary>
    public static Dictionary<string, List<Synopsis>> ParseUsageDump(string text)
    {
        var result = new Dictionary<string, List<Synopsis>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in SplitSynopses(text.Replace("\r", "").Split('\n')))
        {
            var syn = SynopsisParser.Parse(raw);
            if (syn.Command.Length == 0 || syn.Command.StartsWith('[') || syn.Command.StartsWith('-')) continue;
            if (!result.TryGetValue(syn.Command, out var list)) result[syn.Command] = list = [];
            list.Add(syn);
        }
        return result;
    }

    /// <summary>Joins wrapped usage lines; a new synopsis starts at each line beginning with "VBoxManage".</summary>
    public static IEnumerable<string> SplitSynopses(IEnumerable<string> lines)
    {
        string? current = null;
        foreach (var line in lines)
        {
            if (Underline().IsMatch(line)) continue;
            if (UsageStart().IsMatch(line))
            {
                if (current != null) yield return current;
                current = line.Trim();
            }
            else if (current != null)
            {
                if (line.Trim().Length == 0) { yield return current; current = null; }
                else current += " " + line.Trim();
            }
        }
        if (current != null) yield return current;
    }

    /// <summary>A heading underlined with dashes, followed by a usage line, names that usage line.</summary>
    private static void ParseTitles(string[] lines, int start, string command, Dictionary<string, string> into)
    {
        for (int i = start + 1; i + 1 < lines.Length; i++)
        {
            if (!Underline().IsMatch(lines[i + 1]) || lines[i].Trim().Length == 0) continue;
            string title = lines[i].Trim();
            var block = lines.Skip(i + 2).TakeWhile((l, k) => k < 40 && !(k > 0 && Underline().IsMatch(l)));
            foreach (var raw in SplitSynopses(block))
            {
                var syn = SynopsisParser.Parse(raw);
                if (syn.Command.Equals(command, StringComparison.OrdinalIgnoreCase) && !into.ContainsKey(syn.Path))
                    into[syn.Path] = title;
            }
        }
    }

    private static void ParseOptionHelp(string[] lines, int start, Dictionary<string, string> into)
    {
        for (int i = start; i < lines.Length; i++)
        {
            var m = OptionEntry().Match(lines[i]);
            if (!m.Success) continue;
            // Usage lines repeated inside the description start with "[" or "VBoxManage", so they never match here.
            int indent = m.Groups[1].Length;
            string key = m.Groups[2].Value.Replace("<N>", "").Replace("<X>", "");
            var body = new List<string>();
            int j = i + 1;
            for (; j < lines.Length; j++)
            {
                string l = lines[j];
                if (l.Trim().Length == 0) { body.Add(""); continue; }
                int ind = l.Length - l.TrimStart().Length;
                if (ind <= indent) break;
                body.Add(l.Trim());
            }
            string textBody = Regex.Replace(string.Join('\n', body).Trim(), @"\n{2,}", "\n\n");
            textBody = Regex.Replace(textBody, @"(?<!\n)\n(?!\n)", " ");
            if (textBody.Length > 0 && !into.ContainsKey(key)) into[key] = textBody;
            i = j - 1;
        }
    }
}
