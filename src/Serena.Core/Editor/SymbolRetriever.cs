// SymbolRetriever - Phase D1
// Bridge between tools and LspClient for symbol operations.
// Ported from serena/symbol.py LanguageServerSymbol & LanguageServerSymbolRetriever

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serena.Core.Project;
using Serena.Lsp;
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
    /// <remarks>
    /// v1.0.30: re-links <see cref="UnifiedSymbolInformation.Parent"/> on each
    /// child as we recurse. Parent is <c>[JsonIgnore]</c>, so it is lost when
    /// the symbol cache (.serena/cache/&lt;lang&gt;/symbols.json) is deserialized.
    /// Without this, <see cref="UnifiedSymbolInformation.NamePath"/> returns
    /// only the leaf name (e.g. "GetPageThumbnail") instead of the qualified
    /// path ("ThumbnailController/GetPageThumbnail"), which broke
    /// <c>find_symbol</c> for any class-qualified <c>name_path_pattern</c>
    /// on a file-scoped call. Idempotent for in-memory trees where Parent is
    /// already set.
    /// </remarks>
    public static LanguageServerSymbol FromUnified(UnifiedSymbolInformation unified, string relativePath)
    {
        foreach (var child in unified.Children)
        {
            child.Parent = unified;
        }

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

    private readonly Func<CancellationToken, Task<LspClient>> _lspFactory;
    private LspClient? _resolvedLsp;
    private readonly SemaphoreSlim _lspGate = new(1, 1);
    private readonly string _projectRoot;
    private readonly ILogger _logger;
    private readonly SymbolCache<UnifiedSymbolInformation[]>? _symbolCache;
    private readonly Func<ReadyStateSnapshot>? _snapshotFactory;
    private readonly Language? _language;
    private SymbolCacheRefresher? _refresher;

    /// <summary>
    /// Returns true if the file has an extension recognized as analyzable source code.
    /// </summary>
    public static bool CanAnalyzeFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return s_sourceExtensions.Any(e => string.Equals(ext, e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Constructs a retriever bound to an already-started LSP client. Cache-hit
    /// paths still short-circuit without invoking the client.
    /// </summary>
    public LanguageServerSymbolRetriever(
        LspClient lsp, string projectRoot, ILogger logger,
        SymbolCache<UnifiedSymbolInformation[]>? symbolCache = null,
        Func<ReadyStateSnapshot>? snapshotFactory = null)
        : this(_ => Task.FromResult(lsp), projectRoot, logger, symbolCache, snapshotFactory, lsp.Language)
    {
        _resolvedLsp = lsp;
    }

    /// <summary>
    /// Constructs a retriever with a lazy LSP factory. The factory is invoked
    /// only when the request cannot be satisfied from the symbol cache,
    /// enabling cache-first tools to avoid paying Roslyn startup cost on
    /// large repos (10+ minutes for 40k-file solutions).
    /// </summary>
    public LanguageServerSymbolRetriever(
        Func<CancellationToken, Task<LspClient>> lspFactory,
        string projectRoot, ILogger logger,
        SymbolCache<UnifiedSymbolInformation[]>? symbolCache = null,
        Func<ReadyStateSnapshot>? snapshotFactory = null,
        Language? language = null)
    {
        _lspFactory = lspFactory;
        _projectRoot = Path.GetFullPath(projectRoot);
        _logger = logger;
        _symbolCache = symbolCache;
        _snapshotFactory = snapshotFactory;
        _language = language;
    }

    /// <summary>
    /// Wraps an LSP call with the per-request timeout when a snapshot factory
    /// + language are wired in. Falls through unchanged when not (preserves
    /// pre-v1.0.26 behavior for tests/contexts that don't supply readiness).
    /// </summary>
    private Task<T> WithLspTimeoutAsync<T>(Func<CancellationToken, Task<T>> op, CancellationToken ct)
    {
        if (_snapshotFactory is null || _language is null)
        {
            return op(ct);
        }
        return LspTimeout.WithTimeoutAsync(op, _snapshotFactory, _language.Value, _logger, ct);
    }

    /// <summary>
    /// Resolves the LSP client, invoking the factory on first use. Guarded so
    /// concurrent cache-miss callers share a single startup.
    /// </summary>
    private async Task<LspClient> GetLspAsync(CancellationToken ct)
    {
        if (_resolvedLsp is not null)
        {
            return _resolvedLsp;
        }
        await _lspGate.WaitAsync(ct);
        try
        {
            return _resolvedLsp ??= await _lspFactory(ct);
        }
        finally
        {
            _lspGate.Release();
        }
    }

    public async Task<IReadOnlyList<LanguageServerSymbol>> GetSymbolsAsync(
        string relativePath, CancellationToken ct = default)
    {
        string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, relativePath));

        // Cache-first: if we have the file cached, serve it immediately.
        // On fingerprint mismatch we still serve the cached data (stale is
        // acceptable for overview) but trigger a background reindex so the
        // next call is fresh. This matches FindSymbolsByNamePathAsync.
        if (_symbolCache is not null)
        {
            string fingerprint = CacheFingerprint.ForFile(absolutePath);
            var cached = _symbolCache.TryGet(absolutePath, fingerprint);
            if (cached is not null)
            {
                _logger.LogDebug("Symbol cache HIT (fresh) for {Path}", relativePath);
                return cached
                    .Select(s => LanguageServerSymbol.FromUnified(s, relativePath))
                    .ToList();
            }

            // Fingerprint miss but we still have an entry: serve stale + background refresh.
            var stale = _symbolCache.TryGetUnchecked(absolutePath);
            if (stale is not null)
            {
                _logger.LogInformation(
                    "Symbol cache HIT (stale fingerprint) for {Path} — kicking background reindex",
                    relativePath);
                // Only refresh in the background if LSP is already running. In
                // lazy-LSP (cache-first) mode we deliberately do NOT start the
                // LSP just to refresh a stale entry; the user can run
                // `serena-dotnet project index` to rebuild.
                if (TryGetRefresherIfLspRunning() is { } refresher)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await refresher.RefreshIfStaleAsync(absolutePath, CancellationToken.None); }
                        catch (Exception ex) { _logger.LogDebug(ex, "Background reindex failed for {Path}", relativePath); }
                    }, CancellationToken.None);
                }
                return stale
                    .Select(s => LanguageServerSymbol.FromUnified(s, relativePath))
                    .ToList();
            }

            _logger.LogInformation("Symbol cache MISS for {Path} — falling through to LSP", relativePath);
        }

        // Ensure file is open in LS (lazy: starts LSP if not already running)
        var lsp = await GetLspAsync(ct);
        await lsp.OpenFileAsync(absolutePath);

        // Honor the caller's cancellation token so MCP-side cancel actually
        // stops the LSP request and frees CPU. StreamJsonRpc will send
        // $/cancelRequest to Roslyn when the token fires.
        var symbols = await WithLspTimeoutAsync(
            innerCt => lsp.RequestDocumentSymbolsAsync(absolutePath, innerCt),
            ct);

        // Populate cache
        if (_symbolCache is not null)
        {
            string fingerprint = CacheFingerprint.ForFile(absolutePath);
            _symbolCache.Set(absolutePath, fingerprint, [.. symbols]);
        }

        return [.. symbols.Select(s => LanguageServerSymbol.FromUnified(s, relativePath))];
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

            // Search within a directory — prefer cache when populated to avoid
            // waking Roslyn for thousands of files. Cache may be slightly stale;
            // fire refresh in the background and serve from cache immediately.
            if (Directory.Exists(absPath))
            {
                if (_symbolCache is not null && _symbolCache.Count > 0)
                {
                    if (TryGetRefresherIfLspRunning() is { } dirRefresher)
                    {
                        _ = Task.Run(() => dirRefresher.RefreshIfStaleAsync(absPath, CancellationToken.None), CancellationToken.None);
                    }
                    return SearchCachedSymbols(matcher, substringMatching, absPath);
                }
                return await SearchDirectoryAsync(absPath, matcher, substringMatching, ct);
            }

            return [];
        }

        // Project-wide search: prefer the symbol cache (built by `serena-dotnet project index`)
        // to avoid flooding the LSP with thousands of concurrent document-symbol requests.
        // Fire refresh in background so the first call is instant; next call picks up any updates.
        if (_symbolCache is not null && _symbolCache.Count > 0)
        {
            if (TryGetRefresherIfLspRunning() is { } projRefresher)
            {
                _ = Task.Run(() => projRefresher.RefreshIfStaleAsync(scopeAbsPath: null, CancellationToken.None), CancellationToken.None);
            }
            return SearchCachedSymbols(matcher, substringMatching, scopeAbsPath: null);
        }

        // No cache available — guard against catastrophic CPU usage on large repos.
        // Count source files; if too many, refuse with actionable guidance.
        const int LargeRepoThreshold = 2000;
        int fileCount = EnumerateSourceFiles(_projectRoot).Take(LargeRepoThreshold + 1).Count();
        if (fileCount > LargeRepoThreshold)
        {
            throw new InvalidOperationException(
                $"Project-wide find_symbol on a large repo ({fileCount}+ source files) is disabled " +
                $"to prevent CPU overload. Either: (1) run 'serena-dotnet project index .' first to " +
                $"populate the symbol cache, or (2) pass a 'relative_path' to scope the search.");
        }

        return await SearchDirectoryAsync(_projectRoot, matcher, substringMatching, ct);
    }

    /// <summary>
    /// Returns the cache refresher ONLY if the LSP is already resolved/running.
    /// In lazy-LSP (cache-first) mode we must never start the LSP just to
    /// service a background refresh — the whole point of cache-first is to
    /// avoid Roslyn startup when the cache can answer the query.
    /// </summary>
    private SymbolCacheRefresher? TryGetRefresherIfLspRunning()
    {
        if (_resolvedLsp is null || _symbolCache is null)
        {
            return null;
        }
        return _refresher ??= SymbolCacheRefresher.ForLspClient(_projectRoot, _symbolCache, _resolvedLsp, _logger);
    }

    private IReadOnlyList<LanguageServerSymbol> SearchCachedSymbols(
        NamePathMatcher matcher, bool substringMatching, string? scopeAbsPath)
    {
        var results = new List<LanguageServerSymbol>();
        // Cache keys live in canonical (forward-slash) form; normalize scope the same way.
        string? scopeNormalized = scopeAbsPath is null ? null : SymbolCacheKeys.Normalize(scopeAbsPath);
        foreach (string absPath in _symbolCache!.Keys)
        {
            // Filter to scoped directory when caller passed one
            if (scopeNormalized is not null &&
                !absPath.StartsWith(scopeNormalized, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Skip fingerprint validation for bulk search — 43K stat calls
            // would take >60s on Windows. Stale entries are acceptable for
            // discovery; the auto-reindexer keeps the cache fresh between calls.
            var cached = _symbolCache.TryGetUnchecked(absPath);
            if (cached is null)
            {
                continue;
            }
            string relPath = Path.GetRelativePath(_projectRoot, absPath).Replace('\\', '/');
            var symbols = cached.Select(s => LanguageServerSymbol.FromUnified(s, relPath)).ToList();
            results.AddRange(MatchSymbols(symbols, matcher, substringMatching));
        }
        return results;
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

        var lsp = await GetLspAsync(ct);
        await lsp.OpenFileAsync(absolutePath);
        var locations = await WithLspTimeoutAsync(
            innerCt => lsp.RequestReferencesAsync(absolutePath, line, character, ct: innerCt),
            ct);

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve containing symbol for {Path} at ({Line},{Char})", relPath, line, character);
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
        var result = new ConcurrentDictionary<string, Dictionary<string, List<Dictionary<string, object?>>>>();
        var files = EnumerateSourceFiles(absDir).ToList();

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = GetParallelism(), CancellationToken = ct },
            async (filePath, token) =>
            {
                string relPath = Path.GetRelativePath(_projectRoot, filePath).Replace('\\', '/');
                try
                {
                    var overview = await GetSymbolOverviewAsync(relPath, depth, token);
                    if (overview.Count > 0)
                    {
                        result[relPath] = overview;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get symbol overview for {Path}", relPath);
                }
            });

        return new Dictionary<string, Dictionary<string, List<Dictionary<string, object?>>>>(result);
    }

    private async Task<IReadOnlyList<LanguageServerSymbol>> SearchDirectoryAsync(
        string directory, NamePathMatcher matcher, bool substringMatching, CancellationToken ct)
    {
        var results = new ConcurrentBag<LanguageServerSymbol>();
        var files = EnumerateSourceFiles(directory).ToList();

        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = GetParallelism(), CancellationToken = ct },
            async (filePath, token) =>
            {
                string relPath = Path.GetRelativePath(_projectRoot, filePath).Replace('\\', '/');
                try
                {
                    var symbols = await GetSymbolsAsync(relPath, token);
                    foreach (var match in MatchSymbols(symbols, matcher, substringMatching))
                    {
                        results.Add(match);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get symbols for {Path}", relPath);
                }
            });

        return [.. results];
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

        var lsp = await GetLspAsync(ct);
        foreach (var group in byFile)
        {
            string absolutePath = Path.GetFullPath(Path.Combine(_projectRoot, group.Key));
            await lsp.OpenFileAsync(absolutePath);

            foreach (var symbol in group)
            {
                int line = symbol.Raw.SelectionRange?.Start.Line ?? symbol.BodyLocation!.StartLine;
                int character = symbol.Raw.SelectionRange?.Start.Character ?? symbol.BodyLocation!.StartColumn;

                try
                {
                    var hover = await WithLspTimeoutAsync(
                        innerCt => lsp.RequestHoverAsync(absolutePath, line, character, innerCt),
                        ct);
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

    /// <summary>
    /// Returns the max degree of parallelism for LSP-backed directory operations.
    /// Reads the SERENA_LSP_PARALLELISM env var (clamped to 1-16). Default: 2.
    /// Higher values flood Roslyn and peg CPUs; 2 is a balanced default.
    /// </summary>
    private static int GetParallelism()
    {
        string? raw = Environment.GetEnvironmentVariable("SERENA_LSP_PARALLELISM");
        if (int.TryParse(raw, out int value))
        {
            return Math.Clamp(value, 1, 16);
        }
        return 2;
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
