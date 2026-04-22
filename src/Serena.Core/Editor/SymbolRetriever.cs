// SymbolRetriever - Phase D1
// Bridge between tools and LspClient for symbol operations.
// Ported from serena/symbol.py LanguageServerSymbol & LanguageServerSymbolRetriever

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Core.Editor;

/// <summary>
/// Enriched symbol with project-relative path, body access, and parent chain.
/// Wraps <see cref="UnifiedSymbolInformation"/> with Serena-specific context.
/// </summary>
public sealed class LanguageServerSymbol
{
    public UnifiedSymbolInformation Raw { get; }
    public string RelativePath { get; }
    public LanguageServerSymbolLocation? BodyLocation { get; }
    public IReadOnlyList<LanguageServerSymbol> Children { get; }

    public string Name => Raw.Name;
    public SymbolKind Kind => Raw.Kind;
    public string NamePath => Raw.NamePath;
    public int? OverloadIndex => Raw.OverloadIndex;

    private LanguageServerSymbol(
        UnifiedSymbolInformation raw,
        string relativePath,
        LanguageServerSymbolLocation? bodyLocation,
        IReadOnlyList<LanguageServerSymbol> children)
    {
        Raw = raw;
        RelativePath = relativePath;
        BodyLocation = bodyLocation;
        Children = children;
    }

    /// <summary>
    /// Wraps a <see cref="UnifiedSymbolInformation"/> tree into <see cref="LanguageServerSymbol"/> tree.
    /// </summary>
    public static LanguageServerSymbol FromUnified(UnifiedSymbolInformation unified, string relativePath)
    {
        var children = unified.Children
            .Select(c => FromUnified(c, relativePath))
            .ToList();

        var bodyLocation = CreateLocation(unified, relativePath);

        return new LanguageServerSymbol(unified, relativePath, bodyLocation, children);
    }

    /// <summary>
    /// Flattens this symbol and all descendants into a single enumerable.
    /// </summary>
    public IEnumerable<LanguageServerSymbol> Flatten()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var descendant in child.Flatten())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Gets descendants up to the given depth (0 = self only, 1 = self + children, etc.).
    /// </summary>
    public IEnumerable<LanguageServerSymbol> GetDescendants(int depth)
    {
        if (depth <= 0)
        {
            yield break;
        }
        foreach (var child in Children)
        {
            yield return child;
            foreach (var descendant in child.GetDescendants(depth - 1))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// Converts to a dictionary suitable for JSON serialization.
    /// </summary>
    public Dictionary<string, object?> ToDict(bool includeBody = false, int childDepth = 0)
    {
        var dict = new Dictionary<string, object?>
        {
            ["name_path"] = NamePath,
            ["kind"] = Kind.ToString(),
            ["relative_path"] = RelativePath,
        };

        if (BodyLocation is not null)
        {
            dict["body_location"] = new
            {
                start_line = BodyLocation.StartLine,
                end_line = BodyLocation.EndLine,
            };
        }

        if (includeBody && Raw.Body is not null)
        {
            dict["body"] = Raw.Body.GetText();
        }

        if (childDepth > 0 && Children.Count > 0)
        {
            dict["children"] = GroupChildrenByKind(Children, childDepth - 1);
        }

        return dict;
    }

    private static Dictionary<string, List<Dictionary<string, object?>>> GroupChildrenByKind(
        IReadOnlyList<LanguageServerSymbol> children, int remainingDepth)
    {
        var grouped = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var child in children)
        {
            string kind = child.Kind.ToString();
            if (!grouped.TryGetValue(kind, out var list))
            {
                list = [];
                grouped[kind] = list;
            }

            list.Add(BuildChildDict(child, remainingDepth));
        }
        return grouped;
    }

    private static Dictionary<string, object?> BuildChildDict(
        LanguageServerSymbol child, int remainingDepth)
    {
        var childDict = child.ToDict(childDepth: remainingDepth);

        // kind is redundant (already the grouping key) and relative_path
        // is inherited from the parent — omit both to reduce output size.
        childDict.Remove("kind");
        childDict.Remove("relative_path");

        return childDict;
    }

    private static LanguageServerSymbolLocation? CreateLocation(
        UnifiedSymbolInformation unified, string relativePath)
    {
        var range = unified.BodyRange ?? unified.Location?.Range;
        if (range is null)
        {
            return null;
        }
        return new LanguageServerSymbolLocation(
            relativePath,
            range.Start.Line,
            range.Start.Character,
            range.End.Line,
            range.End.Character);
    }
}

/// <summary>
/// Position information for a symbol within a file.
/// </summary>
public sealed record LanguageServerSymbolLocation(
    string RelativePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

/// <summary>
/// Interface for retrieving and searching symbols.
/// </summary>
public interface ISymbolRetriever
{
    /// <summary>
    /// Gets the document symbol tree for a file.
    /// </summary>
    Task<IReadOnlyList<LanguageServerSymbol>> GetSymbolsAsync(
        string relativePath, CancellationToken ct = default);

    /// <summary>
    /// Finds symbols matching a name path pattern.
    /// </summary>
    Task<IReadOnlyList<LanguageServerSymbol>> FindSymbolsByNamePathAsync(
        string pattern, string? relativePath = null, bool substringMatching = false,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the source body for a symbol from the file buffer.
    /// </summary>
    Task<string?> GetSymbolBodyAsync(
        LanguageServerSymbol symbol, CancellationToken ct = default);

    /// <summary>
    /// Finds references to a symbol across the project.
    /// </summary>
    Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        LanguageServerSymbol symbol, int contextLinesBefore = 1, int contextLinesAfter = 1,
        CancellationToken ct = default);

    /// <summary>
    /// Gets a high-level overview of symbols in a file, grouped by kind.
    /// </summary>
    Task<Dictionary<string, List<Dictionary<string, object?>>>> GetSymbolOverviewAsync(
        string relativePath, int depth = 0, CancellationToken ct = default);

    /// <summary>
    /// Gets a high-level overview of symbols for all source files in a directory, grouped by file then kind.
    /// </summary>
    Task<Dictionary<string, Dictionary<string, List<Dictionary<string, object?>>>>> GetDirectoryOverviewAsync(
        string relativeDirPath, int depth = 0, CancellationToken ct = default);

    /// <summary>
    /// Requests hover info for a batch of symbols, grouped by file to minimize I/O.
    /// </summary>
    Task<Dictionary<LanguageServerSymbol, string?>> RequestInfoForSymbolBatchAsync(
        IReadOnlyList<LanguageServerSymbol> symbols, CancellationToken ct = default);

    /// <summary>
    /// Finds the deepest symbol whose body range contains the given position.
    /// </summary>
    Task<LanguageServerSymbol?> FindContainingSymbolAsync(
        string relativePath, int line, int character, CancellationToken ct = default);
}

/// <summary>
/// A reference location with surrounding context.
/// </summary>
public sealed record ReferenceResult
{
    public required string RelativePath { get; init; }
    public required int Line { get; init; }
    public required int Character { get; init; }
    public string? ContextSnippet { get; init; }
    public string? ContainingSymbolName { get; init; }
}

/// <summary>
/// Helpers for extracting multi-line context from source files.
/// </summary>
public static class SourceContext
{
    /// <summary>
    /// Reads a window of lines around <paramref name="line"/> (0-based) and formats them
    /// with line numbers. The reference line is prefixed with <c>&gt;</c>.
    /// Returns <c>null</c> when the file cannot be read.
    /// </summary>
    public static string? RetrieveContentAroundLine(
        string absolutePath, int line, int linesBefore = 1, int linesAfter = 1)
    {
        string[] fileLines;
        try
        {
            fileLines = File.ReadAllLines(absolutePath);
        }
        catch
        {
            return null;
        }

        if (fileLines.Length == 0 || line < 0 || line >= fileLines.Length)
        {
            return null;
        }

        int start = Math.Max(0, line - linesBefore);
        int end = Math.Min(fileLines.Length - 1, line + linesAfter);

        var sb = new System.Text.StringBuilder();
        for (int i = start; i <= end; i++)
        {
            string prefix = i == line ? "> " : "  ";
            sb.Append(prefix);
            // 1-based display numbers
            sb.Append(i + 1);
            sb.Append(": ");
            sb.AppendLine(fileLines[i]);
        }

        // Remove trailing newline
        if (sb.Length > 0 && sb[sb.Length - 1] == '\n')
        {
            sb.Length--;
            if (sb.Length > 0 && sb[sb.Length - 1] == '\r')
            {
                sb.Length--;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Production implementation that uses <see cref="LspClient"/> for symbol operations.
/// Ported from serena/symbol.py LanguageServerSymbolRetriever.
/// </summary>
public sealed class LanguageServerSymbolRetriever : ISymbolRetriever
{
    private static readonly string[] s_sourceExtensions =
        [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go",
         ".rs", ".cpp", ".c", ".h", ".hpp", ".rb", ".swift", ".kt"];

    private readonly LspClient _lsp;
    private readonly string _projectRoot;
    private readonly ILogger _logger;
    private readonly SymbolCache<UnifiedSymbolInformation[]>? _symbolCache;

    /// <summary>
    /// Returns true if the file has an extension recognized as analyzable source code.
    /// </summary>
    public static bool CanAnalyzeFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return s_sourceExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
    }

    public LanguageServerSymbolRetriever(
        LspClient lsp, string projectRoot, ILogger logger,
        SymbolCache<UnifiedSymbolInformation[]>? symbolCache = null)
    {
        _lsp = lsp;
        _projectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
        _symbolCache = symbolCache;
    }

    public async Task<IReadOnlyList<LanguageServerSymbol>> GetSymbolsAsync(
        string relativePath, CancellationToken ct = default)
    {
        string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));

        // Check cache first
        if (_symbolCache is not null)
        {
            string fingerprint = CacheFingerprint.ForFile(absolutePath);
            var cached = _symbolCache.TryGet(absolutePath, fingerprint);
            if (cached is not null)
            {
                return cached
                    .Select(s => LanguageServerSymbol.FromUnified(s, relativePath))
                    .ToList();
            }
        }

        // Ensure file is open in LS
        await _lsp.OpenFileAsync(absolutePath);

        var symbols = await _lsp.RequestDocumentSymbolsAsync(absolutePath, ct);

        // Populate cache
        if (_symbolCache is not null)
        {
            string fingerprint = CacheFingerprint.ForFile(absolutePath);
            _symbolCache.Set(absolutePath, fingerprint, [.. symbols]);
        }

        return symbols
            .Select(s => LanguageServerSymbol.FromUnified(s, relativePath))
            .ToList();
    }

    public async Task<IReadOnlyList<LanguageServerSymbol>> FindSymbolsByNamePathAsync(
        string pattern, string? relativePath = null, bool substringMatching = false,
        CancellationToken ct = default)
    {
        var matcher = NamePathMatcher.Parse(pattern);

        if (relativePath is not null)
        {
            // Search within a specific file
            string absPath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));
            if (File.Exists(absPath))
            {
                var symbols = await GetSymbolsAsync(relativePath, ct);
                return MatchSymbols(symbols, matcher, substringMatching);
            }

            // Search within a directory
            if (Directory.Exists(absPath))
            {
                return await SearchDirectoryAsync(absPath, matcher, substringMatching, ct);
            }

            return [];
        }

        // Search entire project
        return await SearchDirectoryAsync(_projectRoot, matcher, substringMatching, ct);
    }

    public Task<string?> GetSymbolBodyAsync(
        LanguageServerSymbol symbol, CancellationToken ct = default)
    {
        if (symbol.BodyLocation is null)
        {
            return Task.FromResult<string?>(null);
        }

        string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, symbol.RelativePath));
        if (!File.Exists(absolutePath))
        {
            return Task.FromResult<string?>(null);
        }

        string[] lines = File.ReadAllLines(absolutePath);
        var loc = symbol.BodyLocation;

        if (loc.StartLine < 0 || loc.EndLine >= lines.Length)
        {
            return Task.FromResult<string?>(null);
        }

        var body = new SymbolBody(lines, loc.StartLine, loc.StartColumn, loc.EndLine, loc.EndColumn);
        return Task.FromResult<string?>(body.GetText());
    }

    public async Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        LanguageServerSymbol symbol, int contextLinesBefore = 1, int contextLinesAfter = 1,
        CancellationToken ct = default)
    {
        if (symbol.BodyLocation is null)
        {
            return [];
        }

        string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, symbol.RelativePath));

        // Use the selection range (name position) if available, otherwise body start
        int line = symbol.Raw.SelectionRange?.Start.Line ?? symbol.BodyLocation.StartLine;
        int character = symbol.Raw.SelectionRange?.Start.Character ?? symbol.BodyLocation.StartColumn;

        await _lsp.OpenFileAsync(absolutePath);
        var locations = await _lsp.RequestReferencesAsync(absolutePath, line, character, ct: ct);

        // Cache document symbols per file to avoid redundant GetSymbolsAsync calls
        var symbolsByFile = new Dictionary<string, IReadOnlyList<LanguageServerSymbol>>();

        var results = new List<ReferenceResult>();
        foreach (var loc in locations)
        {
            results.Add(await BuildReferenceResultAsync(
                loc, symbolsByFile, contextLinesBefore, contextLinesAfter, ct));
        }

        return results;
    }

    private async Task<ReferenceResult> BuildReferenceResultAsync(
        Location loc, Dictionary<string, IReadOnlyList<LanguageServerSymbol>> symbolsByFile,
        int contextLinesBefore, int contextLinesAfter, CancellationToken ct)
    {
        string refAbsPath = LspClient.UriToPath(loc.Uri);
        string refRelPath = Path.GetRelativePath(_projectRoot, refAbsPath).Replace('\\', '/');

        string? snippet = SourceContext.RetrieveContentAroundLine(
            refAbsPath, loc.Range.Start.Line, contextLinesBefore, contextLinesAfter);

        string? containingName = await ResolveContainingSymbolNameAsync(
            refRelPath, loc.Range.Start.Line, loc.Range.Start.Character, symbolsByFile, ct);

        return new ReferenceResult
        {
            RelativePath = refRelPath,
            Line = loc.Range.Start.Line,
            Character = loc.Range.Start.Character,
            ContextSnippet = snippet,
            ContainingSymbolName = containingName,
        };
    }

    private async Task<string?> ResolveContainingSymbolNameAsync(
        string relPath, int line, int character,
        Dictionary<string, IReadOnlyList<LanguageServerSymbol>> symbolsByFile, CancellationToken ct)
    {
        try
        {
            if (!symbolsByFile.TryGetValue(relPath, out var fileSymbols))
            {
                fileSymbols = await GetSymbolsAsync(relPath, ct);
                symbolsByFile[relPath] = fileSymbols;
            }

            return FindDeepestContainingSymbol(fileSymbols, line, character)?.NamePath;
        }
        catch
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<LanguageServerSymbol?> FindContainingSymbolAsync(
        string relativePath, int line, int character, CancellationToken ct = default)
    {
        var symbols = await GetSymbolsAsync(relativePath, ct);
        return FindDeepestContainingSymbol(symbols, line, character);
    }

    private static LanguageServerSymbol? FindDeepestContainingSymbol(
        IReadOnlyList<LanguageServerSymbol> symbols, int line, int character)
    {
        LanguageServerSymbol? result = null;
        foreach (var symbol in symbols)
        {
            if (symbol.BodyLocation is { } loc &&
                (line > loc.StartLine || (line == loc.StartLine && character >= loc.StartColumn)) &&
                (line < loc.EndLine || (line == loc.EndLine && character <= loc.EndColumn)))
            {
                result = symbol;
                var child = FindDeepestContainingSymbol(symbol.Children, line, character);
                if (child is not null)
                {
                    result = child;
                }
            }
        }

        return result;
    }

    public async Task<Dictionary<string, List<Dictionary<string, object?>>>> GetSymbolOverviewAsync(
        string relativePath, int depth = 0, CancellationToken ct = default)
    {
        var symbols = await GetSymbolsAsync(relativePath, ct);
        var grouped = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var symbol in symbols)
        {
            string kind = symbol.Kind.ToString();
            if (!grouped.ContainsKey(kind))
            {
                grouped[kind] = [];
            }
            grouped[kind].Add(symbol.ToDict(childDepth: depth));
        }

        return grouped;
    }

    public async Task<Dictionary<string, Dictionary<string, List<Dictionary<string, object?>>>>> GetDirectoryOverviewAsync(
        string relativeDirPath, int depth = 0, CancellationToken ct = default)
    {
        string absDir = Path.GetFullPath(Path.Combine(_projectRoot, relativeDirPath));
        var result = new Dictionary<string, Dictionary<string, List<Dictionary<string, object?>>>>();

        foreach (string filePath in EnumerateSourceFiles(absDir))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            string relPath = Path.GetRelativePath(_projectRoot, filePath).Replace('\\', '/');
            try
            {
                var overview = await GetSymbolOverviewAsync(relPath, depth, ct);
                if (overview.Count > 0)
                {
                    result[relPath] = overview;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get symbol overview for {Path}", relPath);
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<LanguageServerSymbol>> SearchDirectoryAsync(
        string directory, NamePathMatcher matcher, bool substringMatching, CancellationToken ct)
    {
        var results = new List<LanguageServerSymbol>();

        foreach (string filePath in EnumerateSourceFiles(directory))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            string relPath = Path.GetRelativePath(_projectRoot, filePath).Replace('\\', '/');
            try
            {
                var symbols = await GetSymbolsAsync(relPath, ct);
                results.AddRange(MatchSymbols(symbols, matcher, substringMatching));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to get symbols for {Path}", relPath);
            }
        }

        return results;
    }

    private static List<LanguageServerSymbol> MatchSymbols(
        IReadOnlyList<LanguageServerSymbol> symbols, NamePathMatcher matcher, bool substringMatching)
    {
        var results = new List<LanguageServerSymbol>();
        foreach (var symbol in symbols)
        {
            foreach (var flat in symbol.Flatten())
            {
                if (matcher.Matches(flat.NamePath, substringMatching))
                {
                    results.Add(flat);
                }
            }
        }
        return results;
    }

    /// <inheritdoc />
    public async Task<Dictionary<LanguageServerSymbol, string?>> RequestInfoForSymbolBatchAsync(
        IReadOnlyList<LanguageServerSymbol> symbols, CancellationToken ct = default)
    {
        var result = new Dictionary<LanguageServerSymbol, string?>();

        // Group by file to minimize OpenFile calls
        var byFile = symbols
            .Where(s => s.BodyLocation is not null || s.Raw.SelectionRange is not null)
            .GroupBy(s => s.RelativePath);

        foreach (var group in byFile)
        {
            string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, group.Key));
            await _lsp.OpenFileAsync(absolutePath);

            foreach (var symbol in group)
            {
                int line = symbol.Raw.SelectionRange?.Start.Line ?? symbol.BodyLocation!.StartLine;
                int character = symbol.Raw.SelectionRange?.Start.Character ?? symbol.BodyLocation!.StartColumn;

                try
                {
                    var hover = await _lsp.RequestHoverAsync(absolutePath, line, character, ct);
                    result[symbol] = ExtractHoverText(hover);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Hover request failed for {Symbol}", symbol.NamePath);
                    result[symbol] = null;
                }
            }
        }

        return result;
    }

    private static string? ExtractHoverText(Hover? hover)
    {
        if (hover?.Contents is null)
        {
            return null;
        }

        // MarkupContent (most common from modern servers)
        if (hover.Contents is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object
                && element.TryGetProperty("value", out var valueProp))
            {
                return valueProp.GetString();
            }

            if (element.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return element.GetString();
            }
        }

        if (hover.Contents is MarkupContent markup)
        {
            return markup.Value;
        }

        return hover.Contents.ToString();
    }

    private IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(CanAnalyzeFile)
            .Where(f =>
            {
                string rel = Path.GetRelativePath(_projectRoot, f);
                return !rel.StartsWith(".git", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains("node_modules", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !rel.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            });
    }
}

/// <summary>
/// Parses and matches name path patterns against symbol name paths.
/// Supports simple names ("method"), relative paths ("Class/method"),
/// absolute paths ("/Class/method"), and overload indices ("method[0]").
/// </summary>
public sealed class NamePathMatcher
{
    private readonly string[] _segments;
    private readonly bool _isAbsolute;

    private NamePathMatcher(string[] segments, bool isAbsolute)
    {
        _segments = segments;
        _isAbsolute = isAbsolute;
    }

    /// <summary>
    /// Parses a name path pattern.
    /// </summary>
    public static NamePathMatcher Parse(string pattern)
    {
        bool isAbsolute = pattern.StartsWith('/');
        string trimmed = pattern.TrimStart('/');
        string[] segments = trimmed.Split('/');
        return new NamePathMatcher(segments, isAbsolute);
    }

    /// <summary>
    /// Tests whether a symbol's name path matches this pattern.
    /// </summary>
    public bool Matches(string namePath, bool substringMatching = false)
    {
        string[] targetSegments = namePath.Split('/');

        if (_isAbsolute)
        {
            // Absolute: must match entire path exactly
            if (targetSegments.Length != _segments.Length)
            {
                return false;
            }
            for (int i = 0; i < _segments.Length; i++)
            {
                if (!SegmentMatches(_segments[i], targetSegments[i], i == _segments.Length - 1 && substringMatching))
                {
                    return false;
                }
            }
            return true;
        }

        if (_segments.Length == 1)
        {
            // Simple name: match the last segment of the target
            string targetLast = targetSegments[^1];
            return SegmentMatches(_segments[0], targetLast, substringMatching);
        }

        // Relative path: must match as a suffix of the target
        if (targetSegments.Length < _segments.Length)
        {
            return false;
        }

        int offset = targetSegments.Length - _segments.Length;
        for (int i = 0; i < _segments.Length; i++)
        {
            if (!SegmentMatches(_segments[i], targetSegments[offset + i], i == _segments.Length - 1 && substringMatching))
            {
                return false;
            }
        }
        return true;
    }

    private static bool SegmentMatches(string pattern, string target, bool substringMatch)
    {
        if (substringMatch)
        {
            // For substring matching, strip any index from both
            string patternBase = StripIndex(pattern);
            string targetBase = StripIndex(target);
            return targetBase.Contains(patternBase, StringComparison.Ordinal);
        }

        return string.Equals(pattern, target, StringComparison.Ordinal);
    }

    private static string StripIndex(string segment)
    {
        int bracket = segment.IndexOf('[');
        return bracket >= 0 ? segment[..bracket] : segment;
    }
}
