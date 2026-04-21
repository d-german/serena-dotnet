// SymbolRetriever - Phase D1
// Bridge between tools and LspClient for symbol operations.
// Ported from serena/symbol.py LanguageServerSymbol & LanguageServerSymbolRetriever

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
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
            if (!grouped.ContainsKey(kind))
            {
                grouped[kind] = [];
            }
            grouped[kind].Add(child.ToDict(childDepth: remainingDepth));
        }
        return grouped;
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
        LanguageServerSymbol symbol, CancellationToken ct = default);

    /// <summary>
    /// Gets a high-level overview of symbols in a file, grouped by kind.
    /// </summary>
    Task<Dictionary<string, List<Dictionary<string, object?>>>> GetSymbolOverviewAsync(
        string relativePath, int depth = 0, CancellationToken ct = default);
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
/// Production implementation that uses <see cref="LspClient"/> for symbol operations.
/// Ported from serena/symbol.py LanguageServerSymbolRetriever.
/// </summary>
public sealed class LanguageServerSymbolRetriever : ISymbolRetriever
{
    private readonly LspClient _lsp;
    private readonly string _projectRoot;
    private readonly ILogger _logger;

    public LanguageServerSymbolRetriever(LspClient lsp, string projectRoot, ILogger logger)
    {
        _lsp = lsp;
        _projectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
    }

    public async Task<IReadOnlyList<LanguageServerSymbol>> GetSymbolsAsync(
        string relativePath, CancellationToken ct = default)
    {
        string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));

        // Ensure file is open in LS
        await _lsp.OpenFileAsync(absolutePath);

        var symbols = await _lsp.RequestDocumentSymbolsAsync(absolutePath, ct);

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
        LanguageServerSymbol symbol, CancellationToken ct = default)
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

        var results = new List<ReferenceResult>();
        foreach (var loc in locations)
        {
            string refAbsPath = LspClient.UriToPath(loc.Uri);
            string refRelPath = Path.GetRelativePath(_projectRoot, refAbsPath).Replace('\\', '/');

            string? snippet = null;
            try
            {
                string[] fileLines = File.ReadAllLines(refAbsPath);
                int refLine = loc.Range.Start.Line;
                if (refLine >= 0 && refLine < fileLines.Length)
                {
                    snippet = fileLines[refLine].Trim();
                }
            }
            catch
            {
                // Skip if file can't be read
            }

            results.Add(new ReferenceResult
            {
                RelativePath = refRelPath,
                Line = loc.Range.Start.Line,
                Character = loc.Range.Start.Character,
                ContextSnippet = snippet,
            });
        }

        return results;
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

    private IEnumerable<string> EnumerateSourceFiles(string directory)
    {
        string[] extensions = [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".java", ".go",
                              ".rs", ".cpp", ".c", ".h", ".hpp", ".rb", ".swift", ".kt"];

        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(f =>
            {
                string ext = Path.GetExtension(f);
                return extensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
            })
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
