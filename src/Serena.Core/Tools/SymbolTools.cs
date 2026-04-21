// Symbol Tools - Phase 6C
// Tools for symbol operations: find_symbol, get_symbols_overview, find_referencing_symbols

using Serena.Core.Editor;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Tools;

[SymbolicRead]
public sealed class FindSymbolTool : ToolBase
{
    public FindSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Retrieves information on all symbols/code entities based on the given name path pattern.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path_pattern", "The name path matching pattern (e.g., 'MyClass/myMethod').", typeof(string), Required: true),
        new("relative_path", "Restrict search to this file or directory.", typeof(string), Required: false, DefaultValue: ""),
        new("depth", "Depth up to which descendants shall be retrieved.", typeof(int), Required: false, DefaultValue: 0),
        new("include_body", "Whether to include the symbol's source code.", typeof(bool), Required: false, DefaultValue: false),
        new("include_info", "Whether to include hover/docstring info.", typeof(bool), Required: false, DefaultValue: false),
        new("substring_matching", "If true, use substring matching for the last element.", typeof(bool), Required: false, DefaultValue: false),
        new("include_kinds", "List of LSP symbol kind integers to include. If not provided, all kinds are included.", typeof(List<int>), Required: false),
        new("exclude_kinds", "List of LSP symbol kind integers to exclude. Takes precedence over include_kinds.", typeof(List<int>), Required: false),
        new("max_matches", "Maximum permitted matches. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePathPattern = GetRequired<string>(arguments, "name_path_pattern");
        string relativePath = GetOptional(arguments, "relative_path", "");
        int depth = GetOptional(arguments, "depth", 0);
        bool includeBody = GetOptional(arguments, "include_body", false);
        bool includeInfo = GetOptional(arguments, "include_info", false);
        bool substringMatching = GetOptional(arguments, "substring_matching", false);
        List<int>? includeKinds = GetOptionalList<int>(arguments, "include_kinds");
        List<int>? excludeKinds = GetOptionalList<int>(arguments, "exclude_kinds");
        int maxMatches = GetOptional(arguments, "max_matches", -1);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);

        // Determine which path to use for LS lookup
        string searchPath = string.IsNullOrEmpty(relativePath) ? "." : relativePath;
        var retriever = await RequireSymbolRetrieverAsync(searchPath, ct);

        var symbols = await retriever.FindSymbolsByNamePathAsync(
            namePathPattern,
            string.IsNullOrEmpty(relativePath) ? null : relativePath,
            substringMatching,
            ct);

        // Apply kind filtering
        var filtered = SymbolKindFilter.FilterByKind(symbols, includeKinds, excludeKinds, s => (int)s.Kind);

        if (maxMatches > 0 && symbols.Count > maxMatches)
        {
            var names = filtered.Take(maxMatches + 5).Select(s => s.NamePath);
            return $"Too many matches ({symbols.Count}). Showing first {maxMatches + 5}:\n" +
                   string.Join("\n", names) +
                   "\nRefine your pattern to narrow results.";
        }

        var symbolList = filtered.ToList();
        if (symbolList.Count == 0)
        {
            return $"No symbols found matching '{namePathPattern}'" +
                   (string.IsNullOrEmpty(relativePath) ? "" : $" in {relativePath}");
        }

        // Build result with optional body and children
        var resultDicts = new List<Dictionary<string, object?>>();
        foreach (var symbol in symbolList)
        {
            var dict = symbol.ToDict(includeBody: includeBody, childDepth: depth);

            if (includeBody && !dict.ContainsKey("body"))
            {
                // Try to load body from file if not already included
                var body = await retriever.GetSymbolBodyAsync(symbol, ct);
                if (body is not null)
                {
                    dict["body"] = body;
                }
            }

            resultDicts.Add(dict);
        }

        // Attach hover info when requested (skip when body is already included)
        if (includeInfo && !includeBody)
        {
            var infoMap = await retriever.RequestInfoForSymbolBatchAsync(symbolList, ct);
            for (int i = 0; i < symbolList.Count; i++)
            {
                if (infoMap.TryGetValue(symbolList[i], out string? info) && info is not null)
                {
                    resultDicts[i]["info"] = info;
                }
            }
        }

        var grouped = SymbolDictGrouper.GroupByMultiple(resultDicts, ["relative_path", "kind"]);
        string result = ToolResultFormatter.FormatGroupedSymbols(grouped, maxChars: -1);
        return LimitLength(result, maxAnswerChars,
            () => string.Join("\n", symbolList.Select(s => s.NamePath)));
    }
}

[SymbolicRead]
public sealed class GetSymbolsOverviewTool : ToolBase
{
    public GetSymbolsOverviewTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Get a high-level understanding of the code symbols in a file.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file to get the overview of.", typeof(string), Required: true),
        new("depth", "Depth up to which descendants of top-level symbols shall be retrieved.", typeof(int), Required: false, DefaultValue: 0),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int depth = GetOptional(arguments, "depth", 0);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);

        string root = RequireProjectRoot();
        string absPath = Path.GetFullPath(Path.Combine(root, relativePath));

        if (Directory.Exists(absPath))
        {
            string? firstFile = Directory.EnumerateFiles(absPath, "*", SearchOption.AllDirectories)
                .FirstOrDefault(LanguageServerSymbolRetriever.CanAnalyzeFile);

            if (firstFile is null)
            {
                return $"No analyzable source files found in {relativePath}";
            }

            string firstRelative = Path.GetRelativePath(root, firstFile).Replace('\\', '/');
            var dirRetriever = await RequireSymbolRetrieverAsync(firstRelative, ct);
            var dirOverview = await dirRetriever.GetDirectoryOverviewAsync(relativePath, depth, ct);

            if (dirOverview.Count == 0)
            {
                return $"No symbols found in {relativePath}";
            }

            string dirResult = ToolResultFormatter.FormatDirectoryOverview(dirOverview, maxChars: -1);
            return LimitLength(dirResult, maxAnswerChars,
                () => FormatOverviewDepthZero(dirOverview.Values.SelectMany(o => o).ToDictionary(kv => kv.Key, kv => kv.Value)),
                () => FormatKindCounts(dirOverview.Values.SelectMany(o => o).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }

        var retriever = await RequireSymbolRetrieverAsync(relativePath, ct);
        var overview = await retriever.GetSymbolOverviewAsync(relativePath, depth, ct);

        if (overview.Count == 0)
        {
            return $"No symbols found in {relativePath}";
        }

        string fileResult = ToolResultFormatter.FormatSymbolOverview(overview, maxChars: -1);
        return LimitLength(fileResult, maxAnswerChars,
            () => FormatOverviewDepthZero(overview),
            () => FormatKindCounts(overview));
    }

    private static string FormatOverviewDepthZero(
        Dictionary<string, List<Dictionary<string, object?>>> overview)
    {
        var compact = new Dictionary<string, List<string>>();
        foreach (var (kind, symbols) in overview)
        {
            compact[kind] = symbols
                .Select(s => s.TryGetValue("name_path", out var np) ? np?.ToString() ?? "?" : "?")
                .ToList();
        }

        return System.Text.Json.JsonSerializer.Serialize(compact);
    }

    private static string FormatKindCounts(
        Dictionary<string, List<Dictionary<string, object?>>> overview)
    {
        return string.Join(", ", overview.Select(kv => $"{kv.Key}: {kv.Value.Count}"));
    }
}

[SymbolicRead]
public sealed class FindReferencingSymbolsTool : ToolBase
{
    public FindReferencingSymbolsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Finds references to the symbol at the given name_path.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path", "Name path of the symbol to find references for.", typeof(string), Required: true),
        new("relative_path", "The relative path to the file containing the symbol.", typeof(string), Required: true),
        new("include_kinds", "List of LSP symbol kind integers to include. If not provided, all kinds are included.", typeof(List<int>), Required: false),
        new("exclude_kinds", "List of LSP symbol kind integers to exclude. Takes precedence over include_kinds.", typeof(List<int>), Required: false),
        new("context_lines_before", "Number of lines of context to include before each reference.", typeof(int), Required: false, DefaultValue: 1),
        new("context_lines_after", "Number of lines of context to include after each reference.", typeof(int), Required: false, DefaultValue: 1),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        List<int>? includeKinds = GetOptionalList<int>(arguments, "include_kinds");
        List<int>? excludeKinds = GetOptionalList<int>(arguments, "exclude_kinds");
        int contextLinesBefore = GetOptional(arguments, "context_lines_before", 1);
        int contextLinesAfter = GetOptional(arguments, "context_lines_after", 1);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);

        var retriever = await RequireSymbolRetrieverAsync(relativePath, ct);

        // Find the symbol first
        var symbols = await retriever.FindSymbolsByNamePathAsync(namePath, relativePath, ct: ct);

        if (symbols.Count == 0)
        {
            return $"Symbol '{namePath}' not found in {relativePath}";
        }

        if (symbols.Count > 1)
        {
            return $"Multiple symbols match '{namePath}' in {relativePath}: " +
                   string.Join(", ", symbols.Select(s => s.NamePath)) +
                   ". Please provide a more specific name_path.";
        }

        IReadOnlyList<ReferenceResult> references = await retriever.FindReferencesAsync(
            symbols[0], contextLinesBefore, contextLinesAfter, ct);

        // Filter references by containing symbol kind when requested
        if (includeKinds is not null || excludeKinds is not null)
        {
            references = await FilterReferencesByKindAsync(
                retriever, references, includeKinds, excludeKinds, ct);
        }

        if (references.Count == 0)
        {
            return $"No references found for '{namePath}'";
        }

        var refDicts = references.Select(r => new Dictionary<string, object?>
        {
            ["relative_path"] = r.RelativePath,
            ["line"] = r.Line,
            ["character"] = r.Character,
            ["context_snippet"] = r.ContextSnippet,
            ["containing_symbol"] = r.ContainingSymbolName,
        }).ToList();

        string result = ToolResultFormatter.FormatReferences(refDicts, maxChars: -1);
        return LimitLength(result, maxAnswerChars,
            () => FormatReferencesWithoutSnippets(refDicts),
            () => FormatReferenceFileCounts(refDicts),
            () => $"Total references: {refDicts.Count}");
    }

    private static string FormatReferencesWithoutSnippets(List<Dictionary<string, object?>> refDicts)
    {
        var stripped = refDicts.Select(d => d
            .Where(kvp => kvp.Key != "context_snippet")
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value))
            .ToList();
        return System.Text.Json.JsonSerializer.Serialize(stripped);
    }

    private static string FormatReferenceFileCounts(List<Dictionary<string, object?>> refDicts)
    {
        var groups = refDicts
            .GroupBy(d => d.TryGetValue("relative_path", out var p) ? p?.ToString() ?? "?" : "?")
            .Select(g => $"{g.Key}: {g.Count()} refs");
        return string.Join(", ", groups);
    }

    /// <summary>
    /// Resolves the containing symbol kind for each reference and filters by kind.
    /// Loads document symbols per unique file to minimize LSP round-trips.
    /// </summary>
    private static async Task<IReadOnlyList<ReferenceResult>> FilterReferencesByKindAsync(
        ISymbolRetriever retriever,
        IReadOnlyList<ReferenceResult> references,
        List<int>? includeKinds,
        List<int>? excludeKinds,
        CancellationToken ct)
    {
        var symbolsByFile = new Dictionary<string, IReadOnlyList<LanguageServerSymbol>>();
        var filtered = new List<ReferenceResult>();

        foreach (var r in references)
        {
            if (!symbolsByFile.TryGetValue(r.RelativePath, out var fileSymbols))
            {
                try
                {
                    fileSymbols = await retriever.GetSymbolsAsync(r.RelativePath, ct);
                }
                catch
                {
                    fileSymbols = [];
                }
                symbolsByFile[r.RelativePath] = fileSymbols;
            }

            var kind = SymbolKindFilter.FindContainingSymbolKind(fileSymbols, r.Line);
            if (kind is null)
            {
                // Cannot determine kind; include by default
                filtered.Add(r);
                continue;
            }

            int kindInt = (int)kind.Value;
            if (excludeKinds?.Contains(kindInt) == true)
            {
                continue;
            }
            if (includeKinds is not null && !includeKinds.Contains(kindInt))
            {
                continue;
            }
            filtered.Add(r);
        }

        return filtered;
    }
}

/// <summary>
/// Shared helpers for filtering symbols and references by <see cref="SymbolKind"/>.
/// </summary>
internal static class SymbolKindFilter
{
    /// <summary>
    /// Filters a sequence by LSP symbol kind.
    /// <paramref name="excludeKinds"/> takes precedence over <paramref name="includeKinds"/>.
    /// </summary>
    public static IEnumerable<T> FilterByKind<T>(
        IEnumerable<T> symbols,
        List<int>? includeKinds,
        List<int>? excludeKinds,
        Func<T, int> kindSelector)
    {
        if (includeKinds is null && excludeKinds is null)
        {
            return symbols;
        }

        return FilterIterator(symbols, includeKinds, excludeKinds, kindSelector);
    }

    private static IEnumerable<T> FilterIterator<T>(
        IEnumerable<T> symbols,
        List<int>? includeKinds,
        List<int>? excludeKinds,
        Func<T, int> kindSelector)
    {
        foreach (var symbol in symbols)
        {
            int kind = kindSelector(symbol);
            if (excludeKinds?.Contains(kind) == true)
            {
                continue;
            }
            if (includeKinds is not null && !includeKinds.Contains(kind))
            {
                continue;
            }
            yield return symbol;
        }
    }

    /// <summary>
    /// Finds the <see cref="SymbolKind"/> of the deepest symbol whose body range contains
    /// the given line position.
    /// </summary>
    public static SymbolKind? FindContainingSymbolKind(
        IReadOnlyList<LanguageServerSymbol> symbols, int line)
    {
        SymbolKind? result = null;
        foreach (var symbol in symbols)
        {
            if (symbol.BodyLocation is { } loc && line >= loc.StartLine && line <= loc.EndLine)
            {
                result = symbol.Kind;
                var childKind = FindContainingSymbolKind(symbol.Children, line);
                if (childKind is not null)
                {
                    result = childKind;
                }
            }
        }
        return result;
    }
}
