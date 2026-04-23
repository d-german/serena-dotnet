// Tests for SymbolCacheRefresher — the auto-incremental-reindex feature shipped in v1.0.16.
// These tests use a fake reindex delegate to avoid spinning up an actual language server.

using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Project;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Core.Tests;

public sealed class SymbolCacheRefresherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _cacheDir;

    public SymbolCacheRefresherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_refresher_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _cacheDir = Path.Combine(_tempDir, ".cache");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private SymbolCache<UnifiedSymbolInformation[]> NewCache()
    {
        return new SymbolCache<UnifiedSymbolInformation[]>(
            _cacheDir, "test_cache.json", 1, NullLogger.Instance);
    }

    private string WriteFile(string name, string content)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task NoChanges_ReturnsZero_AndDoesNotInvokeReindexer()
    {
        var cache = NewCache();
        string path = WriteFile("a.cs", "class A {}");
        cache.Set(path, CacheFingerprint.ForFile(path), []);

        int reindexCalls = 0;
        var refresher = new SymbolCacheRefresher(_tempDir, cache,
            (_, _) => { reindexCalls++; return Task.FromResult(true); },
            NullLogger.Instance);

        int result = await refresher.RefreshIfStaleAsync(scopeAbsPath: null, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(0, reindexCalls);
    }

    [Fact]
    public async Task DebouncedSecondCall_ReturnsZero()
    {
        var cache = NewCache();
        string path = WriteFile("b.cs", "class B {}");
        // Insert with a wrong fingerprint so the file is considered stale.
        cache.Set(path, "stale-fingerprint", []);

        int reindexCalls = 0;
        var refresher = new SymbolCacheRefresher(_tempDir, cache,
            (_, _) => { reindexCalls++; return Task.FromResult(true); },
            NullLogger.Instance);

        int first = await refresher.RefreshIfStaleAsync(null, CancellationToken.None);
        int second = await refresher.RefreshIfStaleAsync(null, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(1, reindexCalls); // second call short-circuited by debounce
    }

    [Fact]
    public async Task FileModified_TriggersReindex()
    {
        var cache = NewCache();
        string path = WriteFile("c.cs", "class C {}");
        cache.Set(path, "stale-fingerprint", []);

        string? reindexedPath = null;
        var refresher = new SymbolCacheRefresher(_tempDir, cache,
            (p, _) => { reindexedPath = p; return Task.FromResult(true); },
            NullLogger.Instance);

        int result = await refresher.RefreshIfStaleAsync(null, CancellationToken.None);

        Assert.Equal(1, result);
        // Cache normalizes keys to forward slashes on Windows; so the refresher
        // hands the reindexer the normalized path. Compare using same normalization.
        Assert.Equal(path.Replace('\\', '/'), reindexedPath);
    }

    [Fact]
    public async Task ScopeAbsPath_LimitsReindexToMatchingFiles()
    {
        var cache = NewCache();
        string subA = Path.Combine(_tempDir, "subA");
        string subB = Path.Combine(_tempDir, "subB");
        Directory.CreateDirectory(subA);
        Directory.CreateDirectory(subB);

        string fileInA = Path.Combine(subA, "x.cs");
        string fileInB = Path.Combine(subB, "y.cs");
        File.WriteAllText(fileInA, "class X {}");
        File.WriteAllText(fileInB, "class Y {}");

        cache.Set(fileInA, "stale", []);
        cache.Set(fileInB, "stale", []);

        var reindexed = new List<string>();
        var refresher = new SymbolCacheRefresher(_tempDir, cache,
            (p, _) => { lock (reindexed) { reindexed.Add(p); } return Task.FromResult(true); },
            NullLogger.Instance);

        int result = await refresher.RefreshIfStaleAsync(scopeAbsPath: subA, CancellationToken.None);

        Assert.Equal(1, result);
        Assert.Single(reindexed);
        Assert.Equal(fileInA.Replace('\\', '/'), reindexed[0]);
    }

    [Fact]
    public async Task TooManyStaleFiles_LogsAndSkips()
    {
        var cache = NewCache();
        // Create more than MaxInlineReindex (=10) stale entries.
        int totalFiles = SymbolCacheRefresher.MaxInlineReindex + 5;
        for (int i = 0; i < totalFiles; i++)
        {
            string p = WriteFile($"big_{i}.cs", $"class C{i} {{}}");
            cache.Set(p, "stale", []);
        }

        int reindexCalls = 0;
        var refresher = new SymbolCacheRefresher(_tempDir, cache,
            (_, _) => { reindexCalls++; return Task.FromResult(true); },
            NullLogger.Instance);

        int result = await refresher.RefreshIfStaleAsync(null, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal(0, reindexCalls);
    }
}
