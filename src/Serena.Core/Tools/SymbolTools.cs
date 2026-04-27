// Symbol Tools - Phase 6C
// Tools for symbol operations: find_symbol, get_symbols_overview, find_referencing_symbols

using Microsoft.Extensions.Logging;
using Serena.Core.Editor;
using Serena.Lsp;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Tools;

/// <summary>
/// Renders a <see cref="LanguageServerWarmingException"/> as a deterministic
/// JSON string so the agent can detect and react to a warming-state response
/// uniformly across find_symbol, find_referencing_symbols, and
/// get_symbols_overview. The shared formatter keeps wording consistent.
/// </summary>
internal static class SymbolToolWarmingResponse
{
    public static string Format(LanguageServerWarmingException ex) => ex.ToJson();
}

[SymbolicRead]
public sealed class FindSymbolTool : ToolBase
{
    public FindSymbolTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Retrieves information on all symbols/code entities based on the given name path pattern. " +
        "Accepts either `name_path_pattern` (canonical) or `name_path` (alias) for the pattern argument. " +
        "PERFORMANCE: when called WITH a relative_path, this uses a lightweight per-file parser " +
        "and does NOT require Roslyn (works during warmup). When called WITHOUT relative_path " +
        "(solution-wide search) it requires Roslyn and may return a warming status while loading. " +
        "On large solutions (>50 projects): call warm_language_server right after set_active_solution " +
        "to start warmup, prefer search_for_pattern for initial exploration, and call " +
        "get_language_server_status to check readiness before solution-wide symbol queries.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("name_path_pattern", "The name path matching pattern (e.g., 'MyClass/myMethod'). Canonical name; alias `name_path` is also accepted.", typeof(string), Required: false, DefaultValue: ""),
        new("name_path", "Alias for `name_path_pattern`. Provide either one.", typeof(string), Required: false, DefaultValue: ""),
        new("relative_path", "Restrict search to this file or directory.", typeof(string), Required: false, DefaultValue: ""),
        new("depth", "Depth up to which descendants shall be retrieved.", typeof(int), Required: false, DefaultValue: 0),
        new("include_body", "Whether to include the symbol's source code.", typeof(bool), Required: false, DefaultValue: false),
        new("include_info", "Whether to include hover/docstring info.", typeof(bool), Required: false, DefaultValue: false),
        new("substring_matching", "If true, use substring matching for the last element.", typeof(bool), Required: false, DefaultValue: false),
        new("match_overloads", "If true, ignore parent path and match all symbols whose leaf name equals the pattern's last segment. Mutually exclusive with substring_matching.", typeof(bool), Required: false, DefaultValue: false),
        new("include_kinds", "List of LSP symbol kind integers (1..26 per LSP spec) to include. If not provided, all kinds are included.", typeof(List<int>), Required: false),
        new("exclude_kinds", "List of LSP symbol kind integers (1..26 per LSP spec) to exclude. Takes precedence over include_kinds.", typeof(List<int>), Required: false),
        new("max_matches", "Maximum permitted matches. -1 for no limit. Must be -1 or a positive integer.", typeof(int), Required: false, DefaultValue: -1),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        try
        {
        string namePathPattern = GetOptional(arguments, "name_path_pattern", "");
        if (string.IsNullOrWhiteSpace(namePathPattern))
        {
            namePathPattern = GetOptional(arguments, "name_path", "");
        }
        string relativePath = GetOptional(arguments, "relative_path", "");
        int depth = GetOptional(arguments, "depth", 0);
        bool includeBody = GetOptional(arguments, "include_body", false);
        bool includeInfo = GetOptional(arguments, "include_info", false);
        bool substringMatching = GetOptional(arguments, "substring_matching", false);
        bool matchOverloads = GetOptional(arguments, "match_overloads", false);
        List<int>? includeKinds = GetOptionalList<int>(arguments, "include_kinds");
        List<int>? excludeKinds = GetOptionalList<int>(arguments, "exclude_kinds");
        int maxMatches = GetOptional(arguments, "max_matches", -1);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);

        string? validationError = ValidateArguments(namePathPattern, substringMatching, matchOverloads, includeKinds, excludeKinds, maxMatches);
        if (validationError is not null)
        {
            return validationError;
        }

        string searchPath = string.IsNullOrEmpty(relativePath) ? "." : relativePath;
        var retriever = await RequireReadOnlyRetrieverAsync(searchPath, ct);

        IReadOnlyList<LanguageServerSymbol> symbols;
        if (matchOverloads)
        {
            string leaf = namePathPattern.Split('/').Last();
            symbols = await retriever.FindByLeafNameAsync(
                leaf,
                string.IsNullOrEmpty(relativePath) ? null : relativePath,
                ct);
        }
        else
        {
            symbols = await retriever.FindSymbolsByNamePathAsync(
                namePathPattern,
                string.IsNullOrEmpty(relativePath) ? null : relativePath,
                substringMatching,
                ct);
        }

        var filtered = SymbolKindFilter.FilterByKind(symbols, includeKinds, excludeKinds, s => (int)s.Kind);
        var filteredList = filtered.ToList();

        if (maxMatches > 0 && filteredList.Count > maxMatches)
        {
            return FormatTooManyMatches(filteredList, filteredList.Count, maxMatches);
        }

        if (filteredList.Count == 0)
        {
            // v1.0.34: leaf-name fallback. If the exact path returned nothing,
            // try a case-insensitive leaf-name match across the same scope so
            // the agent gets a useful "did you mean…" list instead of a flat
            // "not found" reply.
            string leaf = namePathPattern.Split('/').Last();
            if (!string.IsNullOrEmpty(leaf))
            {
                var leafMatches = await retriever.FindByLeafNameAsync(
                    leaf,
                    string.IsNullOrEmpty(relativePath) ? null : relativePath,
                    ct);
                var leafFiltered = SymbolKindFilter.FilterByKind(
                    leafMatches, includeKinds, excludeKinds, s => (int)s.Kind).ToList();
                if (leafFiltered.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine(
                        $"No exact match for '{namePathPattern}'. Found {leafFiltered.Count} symbols with leaf name '{leaf}':");
                    foreach (var s in leafFiltered)
                    {
                        int startLine = s.BodyLocation?.StartLine ?? 0;
                        int endLine = s.BodyLocation?.EndLine ?? 0;
                        sb.AppendLine(
                            $"- {s.NamePath} ({s.Kind}, {s.RelativePath}:{startLine + 1}-{endLine + 1})");
                    }
                    return sb.ToString().TrimEnd();
                }
            }

            return $"No symbols found matching '{namePathPattern}'" +
                   (string.IsNullOrEmpty(relativePath) ? "" : $" in {relativePath}");
        }

        var resultDicts = await BuildResultDictsAsync(filteredList, retriever, includeBody, includeInfo, depth, ct);

        // v1.0.34: outline-first rendering when bodies are requested. Keeps the
        // outline visible up front so the agent can cheaply see kind, name path,
        // location, and body size for every match before any body bytes are
        // streamed; bodies past per-symbol or cumulative caps are replaced with
        // a deterministic re-fetch hint instead of being silently truncated.
        if (includeBody)
        {
            int perBody = GetMaxInlineBodyBytes();
            int totalBody = perBody * 4;
            string outlineFirst = ToolResultFormatter.FormatSymbolsOutlineFirst(
                resultDicts, perBody, totalBody);
            return LimitLength(outlineFirst, maxAnswerChars,
                () => string.Join("\n", filteredList.Select(s => s.NamePath)));
        }

        var grouped = SymbolDictGrouper.GroupByMultiple(resultDicts, ["relative_path", "kind"]);
        string result = ToolResultFormatter.FormatGroupedSymbols(grouped, maxChars: -1);
        return LimitLength(result, maxAnswerChars,
            () => string.Join("\n", filteredList.Select(s => s.NamePath)));
        }
        catch (LanguageServerWarmingException ex)
        {
            return SymbolToolWarmingResponse.Format(ex);
        }
    }

    /// <summary>
    /// v1.0.34: per-symbol body inline cap for outline-first rendering.
    /// Configurable via SERENA_MAX_INLINE_BODY_BYTES, clamped to [512, 1_048_576].
    /// Total cap (across all matches) is 4× the per-symbol cap.
    /// </summary>
    private static int GetMaxInlineBodyBytes()
    {
        string? raw = Environment.GetEnvironmentVariable("SERENA_MAX_INLINE_BODY_BYTES");
        if (int.TryParse(raw, out int v))
        {
            return Math.Clamp(v, 512, 1_048_576);
        }
        return 8192;
    }

    private static string FormatTooManyMatches(
        IEnumerable<LanguageServerSymbol> filtered, int totalCount, int maxMatches)
    {
        var names = filtered.Take(maxMatches + 5).Select(s => s.NamePath);
        return $"Too many matches ({totalCount}). Showing first {maxMatches + 5}:\n" +
               string.Join("\n", names) +
               "\nRefine your pattern to narrow results.";
    }

    /// <summary>
    /// Validates find_symbol arguments. Returns null on success, or an error string
    /// describing the first violation. T5/T6/T7 reject empty patterns, unsupported
    /// wildcards, out-of-range LSP SymbolKind values, and invalid max_matches.
    /// v1.0.34 adds match_overloads and rejects combination with substring_matching.
    /// </summary>
    private static string? ValidateArguments(
        string namePathPattern,
        bool substringMatching,
        bool matchOverloads,
        IReadOnlyList<int>? includeKinds,
        IReadOnlyList<int>? excludeKinds,
        int maxMatches)
    {
        if (string.IsNullOrWhiteSpace(namePathPattern))
        {
            return "name_path_pattern is required";
        }

        if (matchOverloads && substringMatching)
        {
            return "match_overloads and substring_matching are mutually exclusive";
        }

        if (!substringMatching && (namePathPattern.Contains('*') || namePathPattern.Contains('?')))
        {
            return "wildcards not supported in name_path_pattern; pass substring_matching: true for partial matches";
        }

        string? kindError = ValidateKindList(includeKinds, "include_kinds")
                         ?? ValidateKindList(excludeKinds, "exclude_kinds");
        if (kindError is not null)
        {
            return kindError;
        }

        if (maxMatches != -1 && maxMatches <= 0)
        {
            return "max_matches must be -1 (unlimited) or a positive integer";
        }

        return null;
    }

    private static string? ValidateKindList(IReadOnlyList<int>? kinds, string parameterName)
    {
        if (kinds is null)
        {
            return null;
        }
        foreach (int kind in kinds)
        {
            if (kind < 1 || kind > 26)
            {
                return $"{parameterName} contains invalid LSP SymbolKind value: {kind} (valid: 1-26)";
            }
        }
        return null;
    }

    private static async Task<List<Dictionary<string, object?>>> BuildResultDictsAsync(
        List<LanguageServerSymbol> symbolList, ISymbolRetriever retriever,
        bool includeBody, bool includeInfo, int depth, CancellationToken ct)
    {
        var resultDicts = new List<Dictionary<string, object?>>();
        foreach (var symbol in symbolList)
        {
            var dict = symbol.ToDict(includeBody: includeBody, childDepth: depth);

            if (includeBody && !dict.ContainsKey("body"))
            {
                var body = await retriever.GetSymbolBodyAsync(symbol, ct);
                if (body is not null)
                {
                    dict["body"] = body;
                }
            }

            resultDicts.Add(dict);
        }

        if (includeInfo && !includeBody)
        {
            await AttachHoverInfoAsync(symbolList, resultDicts, retriever, ct);
        }

        return resultDicts;
    }

    private static async Task AttachHoverInfoAsync(
        List<LanguageServerSymbol> symbolList, List<Dictionary<string, object?>> resultDicts,
        ISymbolRetriever retriever, CancellationToken ct)
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
}

[SymbolicRead]
public sealed class GetSymbolsOverviewTool : ToolBase
{
    public GetSymbolsOverviewTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Get a high-level understanding of the code symbols in a file. " +
        "PERFORMANCE: file-scoped (uses the lightweight per-file parser, does NOT require Roslyn). " +
        "Safe to call during warmup. Prefer this + search_for_pattern over solution-wide symbol " +
        "queries while warm_language_server / get_language_server_status report Loading.";

    protected override IReadOnlyList<ToolParameter> ExtractParameters() =>
    [
        new("relative_path", "The relative path to the file to get the overview of.", typeof(string), Required: true),
        new("depth", "Depth up to which descendants of top-level symbols shall be retrieved.", typeof(int), Required: false, DefaultValue: 0),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
        new("auto_descend_containers", "v1.0.34: when depth=0 and the overview only contains namespace/module/package containers, automatically re-fetch with depth=2 so callers see actual types/methods. Default true.", typeof(bool), Required: false, DefaultValue: true),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        try
        {
        string relativePath = GetRequired<string>(arguments, "relative_path");
        int depth = GetOptional(arguments, "depth", 0);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);
        bool autoDescend = GetOptional(arguments, "auto_descend_containers", true);

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
            var dirRetriever = await RequireReadOnlyRetrieverAsync(firstRelative, ct);
            var dirOverview = await dirRetriever.GetDirectoryOverviewAsync(relativePath, depth, ct);

            if (dirOverview.Count == 0)
            {
                return $"No symbols found in {relativePath}";
            }

            // v1.0.34: auto-descend when every file in the directory only exposes
            // namespace/module/package containers at depth 0. Re-fetch with depth=2
            // so callers see the actual types and members instead of an empty shell.
            if (depth == 0 && autoDescend &&
                dirOverview.Values.All(IsOnlyContainers))
            {
                dirOverview = await dirRetriever.GetDirectoryOverviewAsync(relativePath, depth: 2, ct);
            }

            string dirResult = ToolResultFormatter.FormatDirectoryOverview(dirOverview, maxChars: -1);
            return LimitLength(dirResult, maxAnswerChars,
                () => FormatOverviewDepthZero(dirOverview.Values.SelectMany(o => o).ToDictionary(kv => kv.Key, kv => kv.Value)),
                () => FormatKindCounts(dirOverview.Values.SelectMany(o => o).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }

        // Cache-first fast path is built into RequireReadOnlyRetrieverAsync:
        // the retriever consults the symbol cache before starting the LSP,
        // so `GetSymbolOverviewAsync` below is instant on cached files and
        // only falls through to Roslyn on a genuine cache miss.
        if (!File.Exists(absPath))
        {
            return $"File not found: {relativePath}";
        }

        var retriever = await RequireReadOnlyRetrieverAsync(relativePath, ct);
        var overview = await retriever.GetSymbolOverviewAsync(relativePath, depth, ct);

        if (overview.Count == 0)
        {
            return $"No symbols found in {relativePath}";
        }

        // v1.0.34: auto-descend when depth=0 returns only namespace-like containers.
        if (depth == 0 && autoDescend && IsOnlyContainers(overview))
        {
            overview = await retriever.GetSymbolOverviewAsync(relativePath, depth: 2, ct);
        }

        string fileResult = ToolResultFormatter.FormatSymbolOverview(overview, maxChars: -1);
        return LimitLength(fileResult, maxAnswerChars,
            () => FormatOverviewDepthZero(overview),
            () => FormatKindCounts(overview));
        }
        catch (LanguageServerWarmingException ex)
        {
            return SymbolToolWarmingResponse.Format(ex);
        }
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

    /// <summary>
    /// v1.0.34: returns true when the overview's only kinds are namespace-style
    /// containers (Namespace, Module, Package). Used by auto_descend_containers
    /// to detect when depth=0 produced an effectively empty shell that should be
    /// re-fetched at depth=2.
    /// </summary>
    internal static bool IsOnlyContainers(
        Dictionary<string, List<Dictionary<string, object?>>> overview)
    {
        if (overview.Count == 0)
        {
            return false;
        }
        return overview.Keys.All(k =>
            k == "Namespace" || k == "Module" || k == "Package");
    }

    /// <summary>
    /// Previously this class carried a ~55-line <c>TryBuildOverviewFromCache</c>
    /// helper that inlined cache-first logic to dodge LSP startup. That band-aid
    /// is now handled generically by <c>ToolBase.RequireReadOnlyRetrieverAsync</c>
    /// plus the lazy-LSP factory on <see cref="LanguageServerSymbolRetriever"/>,
    /// so the tool code stays small and the optimization applies to every
    /// read-only tool (find_symbol, find_referencing_symbols, etc.) instead of
    /// being special-cased here.
    /// </summary>
}

[SymbolicRead]
public sealed class FindReferencingSymbolsTool : ToolBase
{
    public FindReferencingSymbolsTool(IToolContext context) : base(context) { }

    public override string Description =>
        "Finds references to the symbol at the given name_path. " +
        "PERFORMANCE: ALWAYS requires Roslyn — there is no file-scoped fallback. Will return a " +
        "warming status until get_language_server_status reports Ready. On large solutions: " +
        "call warm_language_server right after set_active_solution; use search_for_pattern for " +
        "call-site discovery while waiting; only call this once Ready. Do not retry immediately " +
        "on a warming response — poll get_language_server_status first.";

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
        try
        {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
        List<int>? includeKinds = GetOptionalList<int>(arguments, "include_kinds");
        List<int>? excludeKinds = GetOptionalList<int>(arguments, "exclude_kinds");
        int contextLinesBefore = GetOptional(arguments, "context_lines_before", 1);
        int contextLinesAfter = GetOptional(arguments, "context_lines_after", 1);
        int maxAnswerChars = GetOptional(arguments, "max_answer_chars", -1);

        // Cache-first: look up the symbol from the on-disk symbol cache with
        // no LSP startup. The LSP is started lazily only if the symbol isn't
        // cached, and unavoidably for FindReferencesAsync below (references are
        // a semantic query that the cache doesn't answer).
        var retriever = await RequireReadOnlyRetrieverAsync(relativePath, ct);
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
                retriever, references, includeKinds, excludeKinds, Logger, ct);
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
        catch (LanguageServerWarmingException ex)
        {
            return SymbolToolWarmingResponse.Format(ex);
        }
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
        ILogger logger,
        CancellationToken ct)
    {
        var symbolsByFile = new Dictionary<string, IReadOnlyList<LanguageServerSymbol>>();
        var filtered = new List<ReferenceResult>();

        foreach (var r in references)
        {
            var fileSymbols = await GetOrLoadFileSymbolsAsync(retriever, symbolsByFile, r.RelativePath, logger, ct);
            if (ShouldIncludeReference(fileSymbols, r.Line, includeKinds, excludeKinds))
            {
                filtered.Add(r);
            }
        }

        return filtered;
    }

    private static async Task<IReadOnlyList<LanguageServerSymbol>> GetOrLoadFileSymbolsAsync(
        ISymbolRetriever retriever,
        Dictionary<string, IReadOnlyList<LanguageServerSymbol>> cache,
        string relativePath,
        ILogger logger,
        CancellationToken ct)
    {
        if (!cache.TryGetValue(relativePath, out var fileSymbols))
        {
            try
            {
                fileSymbols = await retriever.GetSymbolsAsync(relativePath, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to load symbols for {Path}", relativePath);
                fileSymbols = [];
            }
            cache[relativePath] = fileSymbols;
        }
        return fileSymbols;
    }

    private static bool ShouldIncludeReference(
        IReadOnlyList<LanguageServerSymbol> fileSymbols, int line,
        List<int>? includeKinds, List<int>? excludeKinds)
    {
        var kind = SymbolKindFilter.FindContainingSymbolKind(fileSymbols, line);
        if (kind is null)
        {
            return true; // Cannot determine kind; include by default
        }

        int kindInt = (int)kind.Value;
        if (excludeKinds?.Contains(kindInt) == true)
        {
            return false;
        }

        return includeKinds is null || includeKinds.Contains(kindInt);
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
