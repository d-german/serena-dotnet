// Language Server Manager - Phase 5D
// Manages language server lifecycle per project

using Microsoft.Extensions.Logging;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Process;
using Serena.Lsp.Protocol;

namespace Serena.Core.Project;

/// <summary>
/// Manages language server instances for a project.
/// Creates, starts, and stops LSP clients per language.
/// Ported from serena/ls_manager.py LanguageServerManager.
/// </summary>
public sealed class LanguageServerManager : IAsyncDisposable
{
    private const int SymbolCacheVersion = 1;

    private readonly ILogger<LanguageServerManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LanguageServerRegistry _registry;
    private readonly string _projectRoot;
    private readonly Dictionary<Language, LspClient> _clients = [];
    private readonly Dictionary<Language, SymbolCache<UnifiedSymbolInformation[]>> _caches = [];

    public LanguageServerManager(
        string projectRoot,
        LanguageServerRegistry registry,
        ILoggerFactory loggerFactory)
    {
        _projectRoot = projectRoot;
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LanguageServerManager>();
    }

    /// <summary>
    /// Gets or starts a language server for the specified language.
    /// </summary>
    public async Task<LspClient> GetOrStartAsync(
        Language language,
        CustomLsSettings? settings = null,
        CancellationToken ct = default)
    {
        if (_clients.TryGetValue(language, out var existing) && existing.IsRunning)
        {
            return existing;
        }

        var definition = _registry.Get(language)
            ?? throw new InvalidOperationException($"No language server registered for {language}");

        var lsSettings = settings ?? new CustomLsSettings();
        var launchInfo = definition.CreateLaunchInfo(_projectRoot, lsSettings);
        var process = new LanguageServerProcess(
            launchInfo,
            language,
            _loggerFactory.CreateLogger<LanguageServerProcess>(),
            GetLspRequestTimeout());

        var client = new LspClient(
            process,
            _projectRoot,
            language,
            _loggerFactory.CreateLogger<LspClient>());

        await client.StartAsync(
            definition.GetInitializationOptions(_projectRoot, lsSettings),
            definition.GetWorkspaceSettings(_projectRoot, lsSettings),
            ct);

        await definition.PostStartAsync(client, _projectRoot, ct);

        _clients[language] = client;
        EnsureSymbolCache(language);
        _logger.LogInformation("Started language server for {Language}", language);
        return client;
    }

    /// <summary>
    /// Gets the symbol cache for the specified language, or null if no server has been started.
    /// </summary>
    public SymbolCache<UnifiedSymbolInformation[]>? GetSymbolCache(Language language) =>
        _caches.GetValueOrDefault(language);

    /// <summary>
    /// Gets the symbol cache for a language, loading it from disk if needed.
    /// Does NOT start a language server — safe to call on the hot path of a
    /// cache-only lookup. Returns null only if no cache file exists on disk.
    /// </summary>
    public SymbolCache<UnifiedSymbolInformation[]>? GetOrLoadSymbolCache(Language language)
    {
        if (_caches.TryGetValue(language, out var existing))
        {
            return existing;
        }
        EnsureSymbolCache(language);
        var cache = _caches.GetValueOrDefault(language);
        // EnsureSymbolCache always inserts an entry but Load() may have returned
        // false if the cache file doesn't exist. Report null in that case so the
        // caller can fall through to LSP without thinking the cache is empty-but-real.
        if (cache is not null && cache.Count == 0)
        {
            return null;
        }
        return cache;
    }

    /// <summary>
    /// Stops a specific language server.
    /// </summary>
    public async Task StopAsync(Language language, CancellationToken ct = default)
    {
        if (_caches.TryGetValue(language, out var cache))
        {
            cache.Save();
        }

        if (_clients.Remove(language, out var client))
        {
            await client.StopAsync(ct);
            _logger.LogInformation("Stopped language server for {Language}", language);
        }
    }

    /// <summary>
    /// Gets all currently running clients.
    /// </summary>
    public IReadOnlyDictionary<Language, LspClient> RunningClients => _clients;

    /// <summary>
    /// Stops all language servers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var (_, cache) in _caches)
        {
            try
            {
                cache.Save();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error saving symbol cache");
            }
        }
        _caches.Clear();

        foreach (var (language, client) in _clients.ToList())
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing LS for {Language}", language);
            }
        }
        _clients.Clear();
    }

    /// <summary>
    /// Force-kills all running language server processes immediately, bypassing
    /// LSP shutdown handshake. Use when servers are hung or burning CPU.
    /// Caches are still flushed to disk.
    /// </summary>
    public void ForceKillAll()
    {
        foreach (var (_, cache) in _caches)
        {
            try { cache.Save(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error saving symbol cache"); }
        }
        _caches.Clear();

        foreach (var (language, client) in _clients.ToList())
        {
            try { client.ForceKill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error force-killing LS for {Language}", language); }
        }
        _clients.Clear();
    }

    /// <summary>
    /// Returns the per-LSP-request timeout. Reads SERENA_LSP_REQUEST_TIMEOUT_SECONDS
    /// env var (clamped to 10..3600). Default: 300 seconds (5 min).
    /// Protects against truly hung Roslyn calls while allowing slow uncached
    /// symbol requests on very large repos to complete.
    /// </summary>
    private static TimeSpan GetLspRequestTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("SERENA_LSP_REQUEST_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out int value))
        {
            return TimeSpan.FromSeconds(Math.Clamp(value, 10, 3600));
        }
        return TimeSpan.FromSeconds(300);
    }
    private void EnsureSymbolCache(Language language)
    {
        if (_caches.ContainsKey(language))
        {
            return;
        }

        var cacheDir = Path.Combine(
            _projectRoot, ".serena", "cache", language.ToString().ToLowerInvariant());
        var cache = new SymbolCache<UnifiedSymbolInformation[]>(
            cacheDir,
            "symbols.json",
            SymbolCacheVersion,
            _loggerFactory.CreateLogger<SymbolCache<UnifiedSymbolInformation[]>>());
        cache.Load();
        _caches[language] = cache;
    }
}
