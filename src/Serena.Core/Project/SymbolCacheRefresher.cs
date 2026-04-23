// SymbolCacheRefresher - Keeps the symbol cache fresh between full project indexes.
// Detects files whose on-disk fingerprint differs from the cached fingerprint and
// reindexes a small number of them inline. Skips when too many files have changed
// (caller is expected to run `serena-dotnet project index .` for large drift).

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Core.Project;

/// <summary>
/// Reindexes a single file: returns true if the cache was updated, false otherwise.
/// Implementations must not throw — they should log and return false on failure.
/// </summary>
public delegate Task<bool> ReindexFileDelegate(string absolutePath, CancellationToken ct);

/// <summary>
/// Detects and refreshes stale entries in a <see cref="SymbolCache{T}"/> on demand.
/// Debounces concurrent calls so rapid-fire tool invocations don't repeatedly stat
/// the same large set of files. Reindexing is bounded; if too many files are stale
/// the refresher logs a warning and returns without invoking the reindexer.
/// </summary>
public sealed class SymbolCacheRefresher
{
    /// <summary>Minimum interval between scans, in milliseconds.</summary>
    public const int DebounceMs = 5000;

    /// <summary>Maximum number of stale files to reindex inline.</summary>
    public const int MaxInlineReindex = 10;

    /// <summary>Per-file LSP timeout for re-indexing.</summary>
    private static readonly TimeSpan FileTimeout = TimeSpan.FromSeconds(10);

    private readonly string _projectRoot;
    private readonly SymbolCache<UnifiedSymbolInformation[]> _cache;
    private readonly ReindexFileDelegate _reindex;
    private readonly ILogger _logger;

    private long _lastScanTicks;
    private int _scanInFlight;

    public SymbolCacheRefresher(
        string projectRoot,
        SymbolCache<UnifiedSymbolInformation[]> cache,
        ReindexFileDelegate reindex,
        ILogger logger)
    {
        _projectRoot = projectRoot;
        _cache = cache;
        _reindex = reindex;
        _logger = logger;
    }

    /// <summary>
    /// Convenience factory that wires a default LSP-backed reindexer.
    /// </summary>
    public static SymbolCacheRefresher ForLspClient(
        string projectRoot,
        SymbolCache<UnifiedSymbolInformation[]> cache,
        LspClient lsp,
        ILogger logger)
    {
        return new SymbolCacheRefresher(
            projectRoot, cache,
            (path, ct) => DefaultLspReindexAsync(cache, lsp, path, logger, ct),
            logger);
    }

    /// <summary>
    /// Scans the cache for stale entries and reindexes a bounded number inline.
    /// Returns the count of files actually reindexed (0 if debounced, no changes,
    /// or too many stale files). Never throws.
    /// </summary>
    /// <param name="scopeAbsPath">If non-null, only entries whose path starts with
    /// this absolute prefix are checked.</param>
    public async Task<int> RefreshIfStaleAsync(string? scopeAbsPath, CancellationToken ct)
    {
        if (IsDebounced())
        {
            return 0;
        }

        // Single-flight: if another refresh is already running, skip this one.
        if (Interlocked.Exchange(ref _scanInFlight, 1) != 0)
        {
            return 0;
        }

        try
        {
            Interlocked.Exchange(ref _lastScanTicks, Environment.TickCount64);
            var stale = await DetectStaleFilesAsync(scopeAbsPath, ct);
            if (stale.Count == 0)
            {
                return 0;
            }

            if (stale.Count > MaxInlineReindex)
            {
                _logger.LogWarning(
                    "{Count} cached files have changed since last index — skipping inline " +
                    "auto-reindex. Run 'serena-dotnet project index .' to refresh.",
                    stale.Count);
                return 0;
            }

            _logger.LogInformation("Auto-reindexing {Count} stale cached file(s)", stale.Count);
            int reindexed = 0;
            foreach (string path in stale)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                if (await _reindex(path, ct))
                {
                    reindexed++;
                }
            }
            return reindexed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-reindex failed");
            return 0;
        }
        finally
        {
            Interlocked.Exchange(ref _scanInFlight, 0);
        }
    }

    private bool IsDebounced()
    {
        long last = Interlocked.Read(ref _lastScanTicks);
        return last != 0 && (Environment.TickCount64 - last) < DebounceMs;
    }

    private async Task<List<string>> DetectStaleFilesAsync(
        string? scopeAbsPath, CancellationToken ct)
    {
        var stale = new ConcurrentBag<string>();
        // Normalize scope to match the cache's key normalization (forward slashes).
        string? scopeNormalized = scopeAbsPath?.Replace('\\', '/');
        var keys = _cache.Keys
            .Where(k => scopeNormalized is null
                || k.StartsWith(scopeNormalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (keys.Count == 0)
        {
            return [];
        }

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct,
        };

        await Parallel.ForEachAsync(keys, options, (path, _) =>
        {
            try
            {
                string current = CacheFingerprint.ForFile(path);
                if (current.Length == 0)
                {
                    // File was deleted — treat as stale so we drop it on reindex
                    stale.Add(path);
                    return ValueTask.CompletedTask;
                }
                // TryGet returns null when fingerprint mismatches OR entry is missing
                if (_cache.TryGet(path, current) is null)
                {
                    stale.Add(path);
                }
            }
            catch
            {
                // ignore per-file stat failures; just don't mark stale
            }
            return ValueTask.CompletedTask;
        });

        return [.. stale];
    }

    private async Task<bool> ReindexFileAsync(string absolutePath, CancellationToken ct)
    {
        return await _reindex(absolutePath, ct);
    }

    private static async Task<bool> DefaultLspReindexAsync(
        SymbolCache<UnifiedSymbolInformation[]> cache,
        LspClient lsp,
        string absolutePath,
        ILogger logger,
        CancellationToken ct)
    {
        // File deleted since last index: drop from cache.
        if (!File.Exists(absolutePath))
        {
            cache.Remove(absolutePath);
            return true;
        }

        string fingerprint = CacheFingerprint.ForFile(absolutePath);
        if (fingerprint.Length == 0)
        {
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(FileTimeout);

            await lsp.OpenFileAsync(absolutePath);
            try
            {
                var symbols = await lsp.RequestDocumentSymbolsAsync(absolutePath, cts.Token);
                cache.Set(absolutePath, fingerprint, symbols.ToArray());
                return true;
            }
            finally
            {
                try { await lsp.CloseFileAsync(absolutePath); }
                catch { /* best-effort */ }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Auto-reindex timed out for {Path}", absolutePath);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Auto-reindex failed for {Path}", absolutePath);
            return false;
        }
    }
}
