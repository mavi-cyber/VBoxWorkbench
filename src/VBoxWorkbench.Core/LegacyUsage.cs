using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

/// <summary>
/// Reads the usage text of VirtualBox 6.x and older, where bare "VBoxManage" prints a "Commands:" section
/// with one indented block per command instead of one "VBoxManage ..." line per action:
/// <code>
///   controlvm                 &lt;uuid|vmname&gt;
///                             pause|resume|reset|poweroff|savestate|
///                             setlinkstate&lt;1-N&gt; on|off |
/// </code>
/// Each block is rewritten into ordinary usage lines and handed to <see cref="SynopsisParser"/>.
/// </summary>
public static partial class LegacyUsage
{
    [GeneratedRegex(@"^  ([a-z][a-z0-9-]*)(\s+(.*))?$")]
    private static partial Regex BlockStart();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(<\d+-[\dN]+>)?(?=[\s|]|$)")]
    private static partial Regex KeywordStart();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*(\|[a-z][a-z0-9-]*)*\|?$")]
    private static partial Regex PureWordList();

    [GeneratedRegex(@"^([a-z][a-z0-9-]*(?:\|[a-z][a-z0-9-]*)+)\s+(\S.*)$")]
    private static partial Regex LeadingAlternatives();

    [GeneratedRegex(@"^(\s*)\[\s{2,}")]
    private static partial Regex StrayBracket();

    [GeneratedRegex(@"\([^()]*\)")]
    private static partial Regex Parenthetical();

    public static bool LooksLegacy(string usage) =>
        Regex.IsMatch(usage, @"^Commands:\s*$", RegexOptions.Multiline) && Regex.IsMatch(usage, @"^  [a-z][a-z0-9-]*\s{2,}\S", RegexOptions.Multiline);

    public static Dictionary<string, List<Synopsis>> Parse(string usage)
    {
        var result = new Dictionary<string, List<Synopsis>>(StringComparer.OrdinalIgnoreCase);
        var lines = usage.Replace("\r", "").Replace(' ', ' ').Split('\n');
        int start = Array.FindIndex(lines, l => l.Trim() == "Commands:");
        if (start < 0) return result;

        string? name = null;
        var block = new List<string>();
        void Close()
        {
            if (name == null) return;
            foreach (var raw in Rewrite(name, block))
            {
                var syn = SynopsisParser.TryParse(raw);
                if (syn == null || syn.Command.Length == 0) continue;
                if (!result.TryGetValue(syn.Command, out var list)) result[syn.Command] = list = [];
                if (!list.Any(s => s.Raw == syn.Raw)) list.Add(syn);
            }
            name = null;
            block.Clear();
        }

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            // The newer "VBoxManage <command> ..." lines that follow are read by HelpParser.
            if (line.TrimStart().StartsWith("VBoxManage", StringComparison.OrdinalIgnoreCase)) { Close(); continue; }
            var m = BlockStart().Match(line);
            if (m.Success)
            {
                Close();
                name = m.Groups[1].Value;
                block.Add(new string(' ', m.Groups[3].Index) + m.Groups[3].Value);
            }
            else if (name != null && line.Trim().Length > 0) block.Add(line);
            else if (line.Trim().Length == 0 && name != null && i + 1 < lines.Length && BlockStart().IsMatch(lines[i + 1])) Close();
        }
        Close();
        return result;
    }

    /// <summary>Turns one command block into "VBoxManage name ..." usage lines, one per subcommand.</summary>
    private static IEnumerable<string> Rewrite(string name, List<string> block)
    {
        // Notes in parentheses ("(def: 64)", "(comma-separated)") are not syntax.
        string joined = Parenthetical().Replace(string.Join('\n', block), "");
        var lines = joined.Split('\n').Where(l => l.Trim().Length > 0)
            // 6.1 prints "[             USB|NVMe|VirtIO]" in storagectl: the stray bracket is a typo.
            .Select(l => StrayBracket().Replace(l, "$1 ")).ToList();
        if (lines.Count == 0) { yield return $"VBoxManage {name}"; yield break; }

        string first = lines[0].Trim();
        var rest = lines.Skip(1).ToList();
        int baseIndent = rest.Count == 0 ? 0 : rest.Min(l => l.Length - l.TrimStart().Length);

        // "mediumproperty [disk|dvd|floppy] set ..." is followed by "[disk|dvd|floppy] get ..." without the
        // command name: a line that opens like the first one starts another usage of the same command.
        string opener = first.Split(' ')[0];
        int sibling = opener.StartsWith('[') ? rest.FindIndex(l => l.Length - l.TrimStart().Length == baseIndent && l.Trim().Split(' ')[0] == opener) : -1;
        if (sibling >= 0)
        {
            foreach (var r in Rewrite(name, lines.Take(sibling + 1).ToList())) yield return r;
            foreach (var r in Rewrite(name, lines.Skip(sibling + 1).ToList())) yield return r;
            yield break;
        }

        // "list ... vms|runningvms|" continues a value list over several lines: one usage line, no subcommands.
        bool valueList = first.EndsWith('|') && !KeywordStart().IsMatch(first);
        var entries = new List<string>();
        string prefix = "";
        if (KeywordStart().IsMatch(first) && !valueList) entries.Add(first);
        else prefix = first;

        int depth = Depth(first, 0);
        foreach (var line in rest)
        {
            string t = line.Trim();
            int indent = line.Length - line.TrimStart().Length;
            bool starts = !valueList && depth == 0 && indent == baseIndent && KeywordStart().IsMatch(t);
            if (starts) entries.Add(t);
            else if (entries.Count > 0) entries[^1] += " " + t;
            else prefix += " " + t;
            depth = Depth(t, depth);
        }

        if (entries.Count == 0) { yield return $"VBoxManage {name} {prefix}"; yield break; }
        foreach (var entry in entries)
        {
            string e = entry.Trim().TrimEnd('|').TrimEnd();
            if (PureWordList().IsMatch(e))
            {
                foreach (var word in e.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    yield return $"VBoxManage {name} {prefix} {word}";
            }
            else if (LeadingAlternatives().Match(e) is { Success: true } alt)
            {
                // "add|modify --netname <name> ..." is two subcommands with the same arguments.
                foreach (var word in alt.Groups[1].Value.Split('|'))
                    yield return $"VBoxManage {name} {prefix} {word} {alt.Groups[2].Value}";
            }
            else yield return $"VBoxManage {name} {prefix} {e}";
        }
    }

    private static int Depth(string text, int depth)
    {
        foreach (char c in text)
        {
            if (c == '[') depth++;
            else if (c == ']') depth = Math.Max(0, depth - 1);
        }
        return depth;
    }
}
