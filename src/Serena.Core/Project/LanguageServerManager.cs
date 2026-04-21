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
            _loggerFactory.CreateLogger<LanguageServerProcess>());

        var client = new LspClient(
            process,
            _projectRoot,
            language,
            _loggerFactory.CreateLogger<LspClient>());

        await client.StartAsync(
            definition.GetInitializationOptions(_projectRoot, lsSettings),
            definition.GetWorkspaceSettings(_projectRoot, lsSettings),
            ct);

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
