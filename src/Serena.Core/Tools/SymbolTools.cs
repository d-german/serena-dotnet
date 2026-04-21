// Symbol Tools - Phase 6C
// Tools for symbol operations: find_symbol, get_symbols_overview, find_referencing_symbols

using Serena.Core.Editor;

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
        new("max_matches", "Maximum permitted matches. -1 for no limit.", typeof(int), Required: false, DefaultValue: -1),
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePathPattern = GetRequired<string>(arguments, "name_path_pattern");
        string relativePath = GetOptional(arguments, "relative_path", "");
        int depth = GetOptional(arguments, "depth", 0);
        bool includeBody = GetOptional(arguments, "include_body", false);
        bool substringMatching = GetOptional(arguments, "substring_matching", false);
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

        if (maxMatches > 0 && symbols.Count > maxMatches)
        {
            var names = symbols.Take(maxMatches + 5).Select(s => s.NamePath);
            return $"Too many matches ({symbols.Count}). Showing first {maxMatches + 5}:\n" +
                   string.Join("\n", names) +
                   "\nRefine your pattern to narrow results.";
        }

        if (symbols.Count == 0)
        {
            return $"No symbols found matching '{namePathPattern}'" +
                   (string.IsNullOrEmpty(relativePath) ? "" : $" in {relativePath}");
        }

        // Build result with optional body and children
        var resultDicts = new List<Dictionary<string, object?>>();
        foreach (var symbol in symbols)
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

        return ToolResultFormatter.FormatSymbols(resultDicts, maxAnswerChars);
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

        var retriever = await RequireSymbolRetrieverAsync(relativePath, ct);
        var overview = await retriever.GetSymbolOverviewAsync(relativePath, depth, ct);

        if (overview.Count == 0)
        {
            return $"No symbols found in {relativePath}";
        }

        return ToolResultFormatter.FormatSymbolOverview(overview, maxAnswerChars);
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
        new("max_answer_chars", "Max characters for the result. -1 for default.", typeof(int), Required: false, DefaultValue: -1),
    ];

    protected override async Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        string namePath = GetRequired<string>(arguments, "name_path");
        string relativePath = GetRequired<string>(arguments, "relative_path");
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

        var references = await retriever.FindReferencesAsync(symbols[0], ct);

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

        return ToolResultFormatter.FormatReferences(refDicts, maxAnswerChars);
    }
}
