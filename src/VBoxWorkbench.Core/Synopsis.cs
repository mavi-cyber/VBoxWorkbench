using System.Text;
using System.Text.RegularExpressions;

namespace VBoxWorkbench.Core;

public enum ElementKind { Keyword, Positional, Option, Passthrough }

/// <summary>One slot of a usage line: a literal keyword, a positional value, an option, or the "--" passthrough.</summary>
public sealed class SynopsisElement
{
    public ElementKind Kind { get; set; }
    /// <summary>Keyword text, positional label, or option name without its index marker ("--nic" for "--nicN").</summary>
    public string Name { get; set; } = "";
    public bool Required { get; set; }
    /// <summary>The name takes a number suffix, as in --nicN or natpfN.</summary>
    public bool Indexed { get; set; }
    public bool Repeatable { get; set; }
    public bool HasValue { get; set; }
    /// <summary>The usage line wrote the option as --name=value rather than --name value.</summary>
    public bool UsesEquals { get; set; } = true;
    /// <summary>The value is several arguments (for example "server pipe-name"), so it is split on spaces.</summary>
    public bool SplitValue { get; set; }
    public List<string> Choices { get; set; } = [];
    public string Placeholder { get; set; } = "";
    /// <summary>Elements sharing a group id (>= 0) are alternatives of each other.</summary>
    public int ExclusiveGroup { get; set; } = -1;

    public bool IsVm => Kind == ElementKind.Positional && Placeholder.Contains("vmname", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One usage line of a VBoxManage subcommand, parsed into ordered elements.</summary>
public sealed class Synopsis
{
    public string Command { get; set; } = "";
    public string Raw { get; set; } = "";
    public List<SynopsisElement> Elements { get; set; } = [];

    /// <summary>Command plus its literal keywords, for example "snapshot take" or "controlvm natpf".</summary>
    public string Path => string.Join(' ', new[] { Command }.Concat(
        Elements.Where(e => e.Kind == ElementKind.Keyword).Select(e => e.Name)));

    public IEnumerable<SynopsisElement> Options => Elements.Where(e => e.Kind == ElementKind.Option);
}

/// <summary>
/// Parses the usage grammar VBoxManage prints: [optional], &lt;required&gt;, a | b alternatives,
/// --opt=value, --opt value, trailing "..." for repeats. It is deliberately forgiving so that
/// usage lines from newer VirtualBox versions still produce a usable form.
/// </summary>
public static partial class SynopsisParser
{
    private sealed class Node
    {
        public char Bracket;            // '[', '<' or '\0' for plain text
        public string Text = "";
        public List<Node> Children = [];
        public bool Glued;              // opened right after a closing bracket, no space between
        public bool IsGroup => Bracket != '\0';
        public bool IsPipe => !IsGroup && Text == "|";
    }

    private static readonly HashSet<string> PlaceholderWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "uuid", "vmname", "filename", "ovfname", "ovaname", "source-medium", "target-medium", "address",
        "snapshot-name", "password-file", "name", "ip", "hostname", "device-name", "network", "netname",
        "ifname", "netid", "MAC-address", "IO-baseIRQ", "I/O-baseIRQ", "pathname", "screen-ID", "IP-Address",
        "path", "file", "password", "keyword", "group-ID",
    };

    private static readonly HashSet<string> TwoArgumentOptions = ["--set-opt", "--set-opt-hex"];

    [GeneratedRegex(@"^VBoxManage(\.exe)?\s+", RegexOptions.IgnoreCase)]
    private static partial Regex LeadIn();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*N$")]
    private static partial Regex IndexedKeyword();

    /// <summary>The older index marker: --nic&lt;1-N&gt;, --boot&lt;1-4&gt;.</summary>
    [GeneratedRegex(@"<(\d+-[\dN]+|N|X)>$")]
    private static partial Regex RangeSuffix();

    /// <summary>The older glued value: --ip&lt;ipv4&gt; means "--ip value".</summary>
    [GeneratedRegex(@"^(--[A-Za-z0-9-]+)<([^<>]+)>$")]
    private static partial Regex GluedValue();

    [GeneratedRegex(@"^([a-z][a-z0-9-]*)N([a-z]+)$")]
    private static partial Regex GluedKeyword();

    public static Synopsis Parse(string raw)
    {
        string line = Regex.Replace(raw.Replace(' ', ' '), @"\s+", " ").Trim();
        string body = LeadIn().Replace(line, "");
        var nodes = Tokenize(body);
        var syn = new Synopsis { Raw = line };
        if (nodes.Count > 0 && !nodes[0].IsGroup && !nodes[0].Text.StartsWith('-'))
        {
            syn.Command = nodes[0].Text;
            nodes.RemoveAt(0);
        }
        int group = 0;
        ParseTopLevel(nodes, syn.Elements, ref group);
        return syn;
    }

    private static List<Node> Tokenize(string s)
    {
        var root = new Node();
        var stack = new Stack<Node>();
        stack.Push(root);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            stack.Peek().Children.Add(new Node { Text = sb.ToString() });
            sb.Clear();
        }
        int literalDepth = 0;
        bool justClosed = false;
        foreach (char c in s)
        {
            bool afterClose = justClosed;
            justClosed = false;
            if (literalDepth > 0)
            {
                sb.Append(c);
                if (c is '[' or '<') literalDepth++;
                else if (c is ']' or '>') literalDepth--;
                continue;
            }
            switch (c)
            {
                case '[' or '<':
                    // A bracket glued to the text before it is part of that value: "--putenv=name[=value]".
                    if (sb.Length > 0 && sb[^1] != '=')
                    {
                        sb.Append(c);
                        literalDepth = 1;
                        break;
                    }
                    Flush();
                    var g = new Node { Bracket = c, Glued = afterClose };
                    stack.Peek().Children.Add(g);
                    stack.Push(g);
                    break;
                case ']' or '>':
                    Flush();
                    if (stack.Count > 1) stack.Pop();
                    justClosed = true;
                    break;
                case '|':
                    Flush();
                    stack.Peek().Children.Add(new Node { Text = "|" });
                    break;
                case ' ':
                    Flush();
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        Flush();
        return root.Children;
    }

    private static string Flatten(IEnumerable<Node> nodes)
    {
        var parts = new List<string>();
        foreach (var n in nodes)
        {
            if (!n.IsGroup) parts.Add(n.Text);
            else parts.Add((n.Bracket == '[' ? "[" : "<") + Flatten(n.Children) + (n.Bracket == '[' ? "]" : ">"));
        }
        return string.Join(' ', parts).Replace(" ,", ",").Replace(", ", ",").Replace("= ", "=");
    }

    private static List<List<Node>> SplitAlternatives(List<Node> nodes)
    {
        var alts = new List<List<Node>> { new() };
        foreach (var n in nodes)
        {
            if (n.IsPipe) alts.Add([]);
            else alts[^1].Add(n);
        }
        alts.RemoveAll(a => a.Count == 0);
        return alts;
    }

    private static bool IsOptionText(string t) =>
        t.StartsWith("--") || (t.Length == 2 && t[0] == '-' && char.IsLetter(t[1]));

    private static bool StartsWithOption(List<Node> alt)
    {
        if (alt.Count == 0) return false;
        var first = alt[0];
        return first.IsGroup ? StartsWithOption(first.Children) : IsOptionText(first.Text);
    }

    private static bool ContainsOption(List<Node> nodes) =>
        nodes.Any(n => n.IsGroup ? ContainsOption(n.Children) : IsOptionText(n.Text));

    private static bool IsSimpleWords(List<Node> alt) => alt.All(n => !n.IsGroup) && !StartsWithOption(alt);

    private static void ParseTopLevel(List<Node> nodes, List<SynopsisElement> output, ref int group) =>
        ParseSequence(nodes, output, required: true, topLevel: true, ref group);

    private static bool IsOptionNode(Node n) => n.IsGroup ? ContainsOption(n.Children) : IsOptionText(n.Text);

    private static bool CommaJoined(Node a, Node b) =>
        !a.IsPipe && !b.IsPipe && (Flatten([a]).EndsWith(',') || Flatten([b]).StartsWith(','));

    /// <summary>
    /// An unbracketed "a | b | c" stretch of a usage line. Returns the index of its last node, or -1 when
    /// no such stretch starts at <paramref name="i"/>.
    /// </summary>
    private static int PipeRunEnd(List<Node> nodes, int i)
    {
        int j = i;
        while (j + 1 < nodes.Count && CommaJoined(nodes[j], nodes[j + 1])) j++;
        if (j + 1 >= nodes.Count || !nodes[j + 1].IsPipe) return -1;
        while (j + 1 < nodes.Count)
        {
            var next = nodes[j + 1];
            if (next.IsPipe) { j += j + 2 < nodes.Count ? 2 : 1; continue; }
            if (IsOptionNode(next)) break;
            j++;
        }
        return j;
    }

    private static void ParsePipeRun(List<Node> run, List<SynopsisElement> output, ref int group)
    {
        string flat = Flatten(run);
        var alts = SplitAlternatives(run);
        if (flat.Contains(','))
        {
            output.Add(new SynopsisElement { Kind = ElementKind.Positional, Name = flat, Placeholder = flat, Required = true, HasValue = true });
            return;
        }
        if (alts.Any(StartsWithOption))
        {
            ParseGroup(new Node { Bracket = '<', Children = run }, output, required: false, ref group);
            return;
        }
        var texts = alts.Select(a => Flatten(a)).ToList();
        static string Bare(string t) => t.Length > 2 && t[0] == '[' && t[^1] == ']' && !t[1..^1].Contains('[') ? t[1..^1] : t;
        output.Add(new SynopsisElement
        {
            Kind = ElementKind.Positional, Name = "value", Required = true, HasValue = true, SplitValue = true,
            Placeholder = string.Join(" | ", texts),
            // Alternatives that are only a placeholder ("<uuid>") are not offered as ready-made choices.
            Choices = texts.Select(Bare).Where(t => !t.StartsWith('<') && !PlaceholderWords.Contains(t)).ToList(),
        });
    }

    private static void ParseGroup(Node g, List<SynopsisElement> output, bool required, ref int group)
    {
        var children = g.Children;
        string flat = Flatten(children);
        if (flat.Contains(',') && !ContainsOption(children))
        {
            // <[rulename],tcp|udp,[host-IP],hostport,[guest-IP],guestport> and <index,0-N> are typed as one value.
            output.Add(new SynopsisElement
            {
                Kind = ElementKind.Positional, Required = required, HasValue = true, Placeholder = flat, Name = flat,
            });
            return;
        }

        var alts = SplitAlternatives(children);
        if (alts.Count <= 1)
        {
            ParseSequence(children, output, required, topLevel: false, ref group);
            return;
        }

        bool firstIsOption = StartsWithOption(alts[0]) && !alts[0][0].IsGroup;
        bool othersAreOptions = alts.Skip(1).Any(StartsWithOption);

        if (firstIsOption && !othersAreOptions)
        {
            // [--opt= a | b | c] or [--password file|-]: one option, the alternatives are its values.
            string optText = alts[0][0].Text;
            var el = OptionFromText(optText, required);
            var values = new List<string>();
            string head = (InlineValue(optText) + " " + Flatten(alts[0].Skip(1))).Trim();
            if (head.Length > 0) values.Add(head);
            values.AddRange(alts.Skip(1).Select(a => Flatten(a)));
            el.HasValue = true;
            el.UsesEquals = optText.Contains('=');
            ApplyValues(el, values, flat);
            output.Add(el);
            return;
        }

        if (alts.All(IsSimpleWords))
        {
            // <uuid | vmname>, [disk | dvd | floppy], <on | off>
            var words = alts.Select(a => Flatten(a)).ToList();
            bool repeats = words.Any(w => w.EndsWith("..."));
            words = words.Select(w => w.TrimEnd('.')).ToList();
            var el = new SynopsisElement
            {
                Kind = ElementKind.Positional, Required = required, HasValue = true, Repeatable = repeats,
                Placeholder = string.Join(" | ", words), Name = string.Join("|", words),
                Choices = words.Where(w => !PlaceholderWords.Contains(w)).ToList(),
            };
            if (words.Any(w => w.Contains(' ')) || repeats) el.SplitValue = true;
            output.Add(el);
            return;
        }

        // Mixed alternatives: each one is parsed on its own and they share an exclusive group.
        int id = group++;
        int start = output.Count;
        foreach (var alt in alts)
        {
            // "--disk=uuid|filename": a bare word after an option's value is another way to write that value.
            if (IsSimpleWords(alt) && output.Count > start && output[^1] is { Kind: ElementKind.Option, HasValue: true } prev)
            {
                prev.Placeholder += " | " + Flatten(alt);
                continue;
            }
            int before = output.Count;
            ParseSequence(alt, output, required: false, topLevel: false, ref group);
            for (int i = before; i < output.Count; i++)
                if (output[i].ExclusiveGroup < 0) output[i].ExclusiveGroup = id;
        }

        // [--mode=machine | --mode=machinechildren | --mode=all] is one option with three values.
        var added = output.Skip(start).ToList();
        if (added.Count > 1 && added.All(e => e.Kind == ElementKind.Option && e.HasValue && e.Name == added[0].Name))
        {
            var merged = added[0];
            merged.Choices = added.Select(e => e.Placeholder).ToList();
            merged.Placeholder = string.Join(" | ", merged.Choices);
            merged.ExclusiveGroup = -1;
            merged.Required = required;
            output.RemoveRange(start + 1, added.Count - 1);
        }
    }

    private static void ParseSequence(List<Node> nodes, List<SynopsisElement> output, bool required, bool topLevel, ref int group)
    {
        SynopsisElement? lastOption = null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.IsPipe) continue;
            // Inside one bracket, whatever trails an option's value is more of that value:
            // [--groups=group [,group...]], [--cpuid-set=leaf [:subleaf] eax ebx ecx edx].
            if (!topLevel && lastOption != null && (n.IsGroup ? !ContainsOption(n.Children) : !IsOptionText(n.Text) && n.Text != "--"))
            {
                string extra = Flatten([n]);
                if (extra.Contains("...")) lastOption.Repeatable = true;
                bool glue = n.IsGroup && extra.Length > 1 && extra[1] is ',' or ':' or '@';
                lastOption.Placeholder += (glue ? "" : " ") + extra;
                if (!n.IsGroup) lastOption.SplitValue = true;
                continue;
            }
            if (n.IsGroup && n.Glued && output.Count > 0 && !ContainsOption(n.Children))
            {
                // "<megabytes per second>[k|m|g]": a unit suffix belongs to the value before it.
                output[^1].Placeholder += Flatten([n]);
                continue;
            }
            if (topLevel && !IsOptionNode(n) && PipeRunEnd(nodes, i) is var end and >= 0)
            {
                ParsePipeRun(nodes.GetRange(i, end - i + 1), output, ref group);
                i = end;
                continue;
            }
            if (n.IsGroup)
            {
                ParseGroup(n, output, required: n.Bracket == '<' && (topLevel || required), ref group);
                continue;
            }

            string t = n.Text;
            if (t is "..." or ",..." or "[,...]")
            {
                if (output.Count > 0) output[^1].Repeatable = true;
                continue;
            }
            if (t == "--")
            {
                output.Add(new SynopsisElement
                {
                    Kind = ElementKind.Passthrough, Name = "--", HasValue = true, SplitValue = true,
                    Placeholder = Flatten(nodes.Skip(i + 1)).Trim('[', ']'),
                });
                return;
            }
            if (IsOptionText(t))
            {
                // Top-level options written without brackets (encryptvm) are still optional in practice.
                var el = OptionFromText(t, required && !topLevel);
                bool hasEquals = t.Contains('=');
                string value = InlineValue(t);
                List<string>? groupChoices = null;
                // Words that follow belong to the value: "--password file", "--set-opt=dhcp-opt-no value...".
                while (i + 1 < nodes.Count)
                {
                    var next = nodes[i + 1];
                    if (next.IsPipe)
                    {
                        // Top level only: "--type disk|network", "--target <uuid|vmname>|global".
                        if (!topLevel || value.Length == 0 || i + 2 >= nodes.Count || IsOptionNode(nodes[i + 2])) break;
                        groupChoices ??= [value];
                        groupChoices.Add(Flatten([nodes[i + 2]]).Trim('<', '>'));
                        i += 2;
                        continue;
                    }
                    // The older usage style writes "--name <value>" where the newer one writes "--name=value".
                    bool incomplete = value.Length == 0 ? hasEquals || next is { IsGroup: true, Bracket: '<' } : value.EndsWith('=') || value.EndsWith(',');
                    if (next.IsGroup && next.Glued && value.Length > 0 && !ContainsOption(next.Children))
                    {
                        value += Flatten([next]);
                        i++;
                        continue;
                    }
                    if (next.IsGroup)
                    {
                        if (!incomplete) break;
                        if (value.Length == 0)
                        {
                            var galts = SplitAlternatives(next.Children);
                            if (galts.Count > 1 && galts.All(IsSimpleWords)) groupChoices = galts.Select(a => Flatten(a)).ToList();
                        }
                        value += groupChoices != null || (next.Bracket == '<' && !hasEquals) ? Flatten(next.Children) : Flatten([next]);
                        i++;
                        continue;
                    }
                    if ((next.Text.StartsWith('-') && next.Text != "-") || next.Text == "...") break;
                    bool first = value.Length == 0;
                    if (!(first || incomplete || next.Text.StartsWith(',') || !topLevel)) break;
                    value += (first || next.Text.StartsWith(',') || value.EndsWith('=') ? "" : " ") + next.Text;
                    i++;
                }
                if (value.Length > 0)
                {
                    el.HasValue = true;
                    el.UsesEquals = hasEquals;
                    ApplyValues(el, groupChoices ?? [value], value);
                    lastOption = el;
                }
                output.Add(el);
                continue;
            }

            // Plain word.
            bool repeat = t.EndsWith("...");
            string word = repeat ? t[..^3] : t;
            if (word.Length == 0) continue;
            if (topLevel && i + 1 < nodes.Count && nodes[i + 1].IsGroup && Flatten(nodes[i + 1].Children) == "...")
            {
                // "<host-source0> host-source1 [...]": more of the same value.
                i++;
                output.Add(new SynopsisElement
                {
                    Kind = ElementKind.Positional, Name = "more " + word, Placeholder = "more, separated by spaces",
                    HasValue = true, Repeatable = true, SplitValue = true,
                });
            }
            else if (topLevel && GluedKeyword().Match(word) is { Success: true } glued)
            {
                // The help prints "natpfNdelete" for "natpfN delete".
                output.Add(new SynopsisElement { Kind = ElementKind.Keyword, Name = glued.Groups[1].Value, Indexed = true, Required = true });
                output.Add(new SynopsisElement { Kind = ElementKind.Keyword, Name = glued.Groups[2].Value, Required = true });
            }
            else if (topLevel)
            {
                bool indexed = IndexedKeyword().IsMatch(word);
                string kw = indexed ? word[..^1] : word;
                if (RangeSuffix().Match(word) is { Success: true } range) { kw = word[..range.Index]; indexed = true; }
                output.Add(new SynopsisElement { Kind = ElementKind.Keyword, Name = kw, Indexed = indexed, Required = true });
            }
            else
            {
                // Several plain words in one bracket form a single labelled value: <prop-name=prop-value>, [size in GB].
                var words = new List<string> { word };
                while (!repeat && i + 1 < nodes.Count && !nodes[i + 1].IsGroup && !nodes[i + 1].IsPipe && !IsOptionText(nodes[i + 1].Text))
                {
                    i++;
                    string w = nodes[i].Text;
                    if (w.EndsWith("...")) { repeat = true; w = w[..^3]; }
                    if (w.Length > 0) words.Add(w);
                }
                string label = string.Join(' ', words);
                output.Add(new SynopsisElement
                {
                    Kind = ElementKind.Positional, Name = label, Placeholder = label, Required = required,
                    HasValue = true, Repeatable = repeat, SplitValue = repeat,
                });
            }
        }
    }

    private static string InlineValue(string optionText)
    {
        int eq = optionText.IndexOf('=');
        return eq < 0 ? "" : optionText[(eq + 1)..];
    }

    private static SynopsisElement OptionFromText(string text, bool required)
    {
        int eq = text.IndexOf('=');
        string name = eq < 0 ? text : text[..eq];
        bool indexed = false;
        if (RangeSuffix().Match(name) is { Success: true } range) { name = name[..range.Index]; indexed = true; }
        else if (eq < 0 && GluedValue().Match(name) is { Success: true } gv)
            return new SynopsisElement
            {
                Kind = ElementKind.Option, Name = gv.Groups[1].Value, Required = required, HasValue = true, UsesEquals = false,
                Placeholder = gv.Groups[2].Value,
            };
        if (name.EndsWith("<N>")) { name = name[..^3]; indexed = true; }
        else if (name.Length > 3 && (name[^1] is 'N' or 'X') && (char.IsLower(name[^2]) || name[^2] == '-'))
        {
            name = name[..^1];
            indexed = true;
        }
        return new SynopsisElement
        {
            Kind = ElementKind.Option, Name = name, Indexed = indexed, Required = required,
            HasValue = eq >= 0, UsesEquals = eq >= 0,
        };
    }

    private static void ApplyValues(SynopsisElement el, List<string> values, string flat)
    {
        values = values.Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
        if (values.Any(v => v.EndsWith("...")))
        {
            el.Repeatable = true;
            values = values.Select(v => v.TrimEnd('.').TrimEnd()).ToList();
        }
        el.Placeholder = string.Join(" | ", values);
        if (values.Count > 1 && !flat.Contains(','))
        {
            el.Choices = values.Where(v => !PlaceholderWords.Contains(v)).ToList();
            // "server pipe" style choices span several arguments.
            if (el.Choices.Any(v => v.Contains(' '))) el.SplitValue = true;
        }
        if (TwoArgumentOptions.Contains(el.Name)) el.SplitValue = true;
    }
}
