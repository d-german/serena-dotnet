// find_occurrences - where an identifier appears, from the occurrence table the
// index builds, without a language server. The cheap first answer to "who
// calls this?": textual, so it cannot separate same-named members of unrelated
// types, but it returns in milliseconds while find_referencing_symbols waits
// for Roslyn, and it reports the enclosing symbol of every hit.

using System.Text.Json;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Core.Tools;

[SymbolicRead]
public sealed class FindOccurrencesTool : ToolBase
{
    public FindOccurrencesTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Lists where an identifier occurs across the indexed project: file, one-based line, the enclosing symbol, " +
        "whether the line declares it, and the source line. Comes from the occurrence table built at index time, " +
        "so it needs no language server and answers in milliseconds. Textual: same-named members of unrelated " +
        "types are all listed. Use it before changing a signature to see every call site, and use " +
        "find_referencing_symbols when you need Roslyn to disambiguate.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name", "The identifier to look for (exact, case-insensitive).", typeof(string), Required: true),
        new("relative_path", "Restrict results to this file or directory.", typeof(string), Required: false, DefaultValue: ""),
        new("exclude_declarations", "Leave out lines that declare the identifier.", typeof(bool), Required: false, DefaultValue: false),
        new("max_results", "Maximum occurrences to return. -1 for no limit.", typeof(int), Required: false, DefaultValue: 200),
        new("max_answer_chars", "Max characters for the result. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string name = GetRequired<string>(arguments, "name");
        string relativePath = GetOptional(arguments, "relative_path", "");
        bool excludeDeclarations = GetOptional(arguments, "exclude_declarations", false);
        int maxResults = GetOptional(arguments, "max_results", 200);
        int maxChars = GetOptional(arguments, "max_answer_chars", -1);

        string root = RequireProjectRoot();
        string? scope = string.IsNullOrWhiteSpace(relativePath) ? null : ResolvePath(relativePath);

        var hits = new List<Dictionary<string, object?>>();
        bool anyTable = false;
        foreach (var language in CandidateLanguages())
        {
            var cache = Context.Agent.GetSymbolCache(language);
            if (cache is null)
            {
                continue;
            }
            if (cache.Occurrences.RowCount() > 0)
            {
                anyTable = true;
            }

            foreach (var occurrence in cache.Occurrences.Query(name, scope))
            {
                var symbols = cache.TryGetUnchecked(occurrence.Path);
                string[]? source = TryReadLines(occurrence.Path);
                foreach (int line in occurrence.Lines)
                {
                    bool declaration = symbols is not null && IsDeclaredAt(symbols, name, line - 1);
                    if (declaration && excludeDeclarations)
                    {
                        continue;
                    }
                    hits.Add(new Dictionary<string, object?>
                    {
                        ["relative_path"] = Path.GetRelativePath(root, occurrence.Path),
                        ["line"] = line,
                        ["containing_symbol"] = symbols is null ? null : FindEnclosing(symbols, line - 1)?.NamePath,
                        ["is_declaration"] = declaration,
                        ["snippet"] = source is not null && line - 1 < source.Length ? source[line - 1].Trim() : null,
                    });
                }
            }
        }

        if (hits.Count == 0)
        {
            return Task.FromResult(anyTable
                ? $"No indexed occurrence of '{name}'."
                : "The symbol index has no occurrence table yet. Run 'serena-dotnet project index <root>' (0.6.0 or later) and activate the project again.");
        }

        hits.Sort((a, b) =>
        {
            int byFile = string.Compare((string)a["relative_path"]!, (string)b["relative_path"]!, StringComparison.OrdinalIgnoreCase);
            return byFile != 0 ? byFile : ((int)a["line"]!).CompareTo((int)b["line"]!);
        });

        var shown = maxResults < 0 ? hits : hits.Take(maxResults).ToList();
        var payload = new Dictionary<string, object?>
        {
            ["total"] = hits.Count,
            ["files"] = hits.Select(h => (string)h["relative_path"]!).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            ["occurrences"] = shown,
        };
        string result = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        return Task.FromResult(LimitLength(result, maxChars,
            () => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["total"] = hits.Count,
                ["files_with_counts"] = hits.GroupBy(h => (string)h["relative_path"]!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count()),
            }, new JsonSerializerOptions { WriteIndented = true }),
            () => $"{hits.Count} occurrence(s) of '{name}'"));
    }

    private IEnumerable<Language> CandidateLanguages()
    {
        var configured = Context.ActiveProject?.GetConfiguredLanguages() ?? [];
        if (configured.Count > 0)
        {
            return configured;
        }
        return [Language.CSharp, Language.TypeScript];
    }

    private static string[]? TryReadLines(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllLines(path) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static UnifiedSymbolInformation? FindEnclosing(IReadOnlyList<UnifiedSymbolInformation> symbols, int line)
    {
        foreach (var symbol in symbols)
        {
            int? start = symbol.StartLine;
            int? end = symbol.EndLine;
            if (start is null || end is null || line < start || line > end)
            {
                continue;
            }
            return FindEnclosing(symbol.Children, line) ?? symbol;
        }
        return null;
    }

    internal static bool IsDeclaredAt(IReadOnlyList<UnifiedSymbolInformation> symbols, string name, int line)
    {
        foreach (var symbol in symbols)
        {
            string leaf = symbol.Name;
            int bracket = leaf.IndexOf('[');
            if (bracket >= 0)
            {
                leaf = leaf[..bracket];
            }
            int declaredLine = symbol.SelectionRange?.Start.Line ?? symbol.StartLine ?? -1;
            if (declaredLine == line && leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (symbol.Children.Count > 0 && IsDeclaredAt(symbol.Children, name, line))
            {
                return true;
            }
        }
        return false;
    }
}
