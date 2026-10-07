using System.Text;

namespace VBoxWorkbench.Core;

/// <summary>A value the user gave for one synopsis element.</summary>
public sealed record ElementValue(SynopsisElement Element, string Value, string Index = "", bool Enabled = true);

/// <summary>A fully built VBoxManage invocation, kept as an argument list so nothing goes through a shell.</summary>
public sealed class BuiltCommand
{
    public List<string> Args { get; init; } = [];
    public string Title { get; init; } = "";
    public bool Dangerous { get; init; }
    /// <summary>Set for tools that are not VBoxManage subcommands (vboximg-mount).</summary>
    public string? Program { get; init; }
    public bool Runnable { get; init; } = true;

    public string Display => (Program ?? "VBoxManage") + " " + string.Join(' ', Args.Select(CommandLine.Quote));
}

public static class CommandLine
{
    /// <summary>Quotes an argument for display and for pasting into a terminal.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length == 0) return "\"\"";
        bool needs = arg.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^' or ';' or '(' or ')');
        return needs ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;
    }

    /// <summary>Splits on spaces while keeping "quoted parts" together.</summary>
    public static List<string> Split(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        foreach (char c in text)
        {
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0 || any) result.Add(sb.ToString());
                sb.Clear();
                any = false;
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) result.Add(sb.ToString());
        return result;
    }

    /// <summary>Builds the argument list for a synopsis from the values in synopsis order.</summary>
    public static List<string> Build(Synopsis synopsis, IEnumerable<ElementValue> values)
    {
        var args = new List<string> { synopsis.Command };
        foreach (var v in values)
        {
            if (!v.Enabled) continue;
            var e = v.Element;
            string name = e.Name + (e.Indexed ? v.Index : "");
            switch (e.Kind)
            {
                case ElementKind.Keyword:
                    args.Add(name);
                    break;
                case ElementKind.Positional:
                    if (v.Value.Length == 0) break;
                    if (e.SplitValue || e.Repeatable) args.AddRange(Split(v.Value));
                    else args.Add(v.Value);
                    break;
                case ElementKind.Passthrough:
                    args.Add("--");
                    args.AddRange(Split(v.Value));
                    break;
                case ElementKind.Option:
                    if (!e.HasValue) { args.Add(name); break; }
                    var parts = e.SplitValue ? Split(v.Value) : [v.Value];
                    if (parts.Count == 0) parts.Add("");
                    if (e.UsesEquals) args.Add(name + "=" + parts[0]);
                    else { args.Add(name); args.Add(parts[0]); }
                    args.AddRange(parts.Skip(1));
                    break;
            }
        }
        return args;
    }
}
