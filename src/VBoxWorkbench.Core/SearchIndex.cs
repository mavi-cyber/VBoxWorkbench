namespace VBoxWorkbench.Core;

/// <summary>One thing the search bar can lead to: a synopsis, optionally focused on one option or prefilled.</summary>
public sealed class SearchEntry
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public CommandDoc Doc { get; init; } = null!;
    public Synopsis Synopsis { get; init; } = null!;
    public string? FocusOption { get; init; }
    public Dictionary<string, string>? Presets { get; init; }
    public bool Curated { get; init; }
    internal string Haystack = "";
}

/// <summary>
/// Search over curated phrases plus every synopsis and every option of the installed version,
/// so nothing VBoxManage can do is unreachable.
/// </summary>
public sealed class SearchIndex
{
    private readonly List<SearchEntry> _entries = [];

    public IReadOnlyList<SearchEntry> Entries => _entries;

    public static string TitleOf(CommandDoc doc, Synopsis s)
    {
        if (doc.Titles.TryGetValue(s.Path, out var t)) return t;
        if (doc.Synopses.Count(x => x.Path == s.Path) <= 1 && s.Path == doc.Name && doc.Summary.Length > 0) return doc.Summary;
        string kw = s.Path.Length > doc.Name.Length ? s.Path[(doc.Name.Length + 1)..] : "";
        return kw.Length > 0 ? $"{doc.Name}: {kw}" : doc.Summary.Length > 0 ? doc.Summary : doc.Name;
    }

    public SearchIndex(Catalog catalog)
    {
        foreach (var p in Curation.Phrases)
        {
            foreach (var doc in catalog.Commands)
            {
                // A phrase names its option the 7.x way; older versions spell it differently (--nat-pf, --natpf).
                string? needs = p.Contains?.TrimEnd('=');
                var syn = doc.Synopses.FirstOrDefault(s => s.Path == p.Path && (needs == null || OptionNames.Find(s, needs) != null));
                if (syn == null) continue;
                var presets = OptionNames.Remap(syn, p.Presets);
                _entries.Add(new SearchEntry
                {
                    Title = p.Text, Subtitle = syn.Path, Doc = doc, Synopsis = syn, Presets = presets, Curated = true,
                    FocusOption = presets?.Keys.FirstOrDefault(),
                    Haystack = (p.Text + " " + syn.Path + " " + string.Join(' ', presets?.Keys ?? Enumerable.Empty<string>())).ToLowerInvariant(),
                });
                break;
            }
        }

        foreach (var doc in catalog.Commands)
        {
            foreach (var syn in doc.Synopses)
            {
                string title = TitleOf(doc, syn);
                _entries.Add(new SearchEntry
                {
                    Title = title, Subtitle = syn.Path, Doc = doc, Synopsis = syn,
                    Haystack = (title + " " + syn.Path + " " + doc.Summary + " " + Curation.RoomTitle(Curation.RoomOf(doc.Name))).ToLowerInvariant(),
                });
                foreach (var opt in syn.Options)
                {
                    string help = doc.HelpFor(opt);
                    string first = FirstSentence(help);
                    _entries.Add(new SearchEntry
                    {
                        Title = first.Length > 0 ? first : $"{syn.Path} {opt.Name}",
                        Subtitle = $"{syn.Path} {opt.Name}{(opt.Indexed ? "N" : "")}",
                        Doc = doc, Synopsis = syn, FocusOption = opt.Name,
                        Haystack = (opt.Name + " " + opt.Name.Replace("-", "") + " " + syn.Path + " " + first + " " + string.Join(' ', opt.Choices)).ToLowerInvariant(),
                    });
                }
            }
        }
    }

    public static string FirstSentence(string text)
    {
        if (text.Length == 0) return "";
        int nl = text.IndexOf('\n');
        string para = nl > 0 ? text[..nl] : text;
        int dot = para.IndexOf(". ", StringComparison.Ordinal);
        string s = dot > 0 ? para[..(dot + 1)] : para;
        return s.Length > 140 ? s[..137] + "..." : s;
    }

    public List<SearchEntry> Search(string query, int limit = 12)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return _entries.Where(e => e.Curated).Take(limit).ToList();
        var scored = new List<(int Score, int Order, SearchEntry Entry)>();
        for (int i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            int score = 0;
            bool all = true;
            string title = e.Title.ToLowerInvariant();
            foreach (var w in words)
            {
                int at = e.Haystack.IndexOf(w, StringComparison.Ordinal);
                if (at < 0) { all = false; break; }
                score += 10;
                if (title.Contains(w)) score += 6;
                // Whole-word and word-start matches beat matches inside a word.
                if (at == 0 || !char.IsLetterOrDigit(e.Haystack[at - 1])) score += 5;
            }
            if (!all) continue;
            if (e.Curated) score += 25;
            if (e.FocusOption == null) score += 4;
            scored.Add((score, i, e));
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Order).Take(limit).Select(s => s.Entry).ToList();
    }
}
