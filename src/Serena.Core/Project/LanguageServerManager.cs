// Language Server Manager - Phase 5D
// Manages language server lifecycle per project

using System.Collections.Concurrent;
using System.Diagnostics;
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
public sealed class LanguageServerManager : IAsyncDisposable, IServerReadyStateSink
{
    private const int SymbolCacheVersion = 1;

    private readonly ILogger<LanguageServerManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LanguageServerRegistry _registry;
    private readonly string _projectRoot;
    private readonly Dictionary<Language, LspClient> _clients = [];
    private readonly Dictionary<Language, SymbolCache<UnifiedSymbolInformation[]>> _caches = [];
    private readonly ConcurrentDictionary<Language, ReadyStateRecord> _readyStates = new();
    // v1.0.27: per-language CTS owns the lifetime of background warmup
    // (PostStartAsync). Cancelled + disposed in StopAsync/RestartAsync so a
    // restart can interrupt an in-flight Roslyn load.
    private readonly ConcurrentDictionary<Language, CancellationTokenSource> _languageCts = new();
    // v1.0.27: per-language gate so two parallel first-callers don't both
    // race past the TryGetValue check and start two LSP processes.
    private readonly ConcurrentDictionary<Language, SemaphoreSlim> _startGates = new();

    /// <summary>
    /// Mutable internal record so MarkLoading/MarkReady can update a single
    /// instance without rebuilding it. <see cref="GetReadyState"/> returns an
    /// immutable <see cref="ReadyStateSnapshot"/> derived from this record.
    /// </summary>
    private sealed class ReadyStateRecord
    {
        public WorkspaceReadyState State;
#pragma warning disable CS0649 // Reserved for future progress reporting once Roslyn exposes per-project load events.
        public int? ProjectsLoaded;
#pragma warning restore CS0649
        public int? ProjectsTotal;
        public string? ScopeDescription;
        public long StartedAtTicks;
    }

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
    /// v1.0.27: workspace warmup (PostStartAsync — e.g. Roslyn solution/open + WaitForIndexingAsync)
    /// runs on a background task. This method returns once the JSON-RPC handshake
    /// completes (seconds), with readiness state set to <see cref="WorkspaceReadyState.Loading"/>.
    /// Callers must consult <see cref="GetReadyState"/> (or use the pre-flight check in
    /// SymbolRetriever) before issuing semantic LSP requests.
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

        var gate = _startGates.GetOrAdd(language, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate — a parallel caller may have started already.
            if (_clients.TryGetValue(language, out existing) && existing.IsRunning)
            {
                return existing;
            }

            var definition = _registry.Get(language)
                ?? throw new InvalidOperationException($"No language server registered for {language}");

            var lsSettings = settings ?? new CustomLsSettings();
            // Inject self as the readiness sink so MvpLanguageServers can report
            // workspace-load lifecycle without taking a reverse dep on Serena.Core.
            // Caller-supplied sink wins so tests can substitute.
            lsSettings.ReadyStateSink ??= this;
            var launchInfo = definition.CreateLaunchInfo(_projectRoot, lsSettings);
            var process = new LanguageServerProcess(
                launchInfo,
                language,
                _loggerFactory.CreateLogger<LanguageServerProcess>(),
                GetLspRequestTimeout(),
                launchInfo.PoliteMode);

            var client = new LspClient(
                process,
                _projectRoot,
                language,
                _loggerFactory.CreateLogger<LspClient>());

            await client.StartAsync(
                definition.GetInitializationOptions(_projectRoot, lsSettings),
                definition.GetWorkspaceSettings(_projectRoot, lsSettings),
                ct);

            // Sync-mark Loading BEFORE detaching PostStartAsync so any caller that
            // checks readiness immediately after this method returns sees Loading,
            // not NotStarted. MvpLanguageServers will refine projectsTotal/scope
            // when it begins its own work, but the state transition is owned here.
            MarkLoading(language);

            // Per-language CTS owns the warmup task lifetime.
            var warmupCts = new CancellationTokenSource();
            _languageCts[language] = warmupCts;

            // Fire-and-forget warmup. Exceptions are swallowed (logged + state
            // flipped to Failed) to prevent UnobservedTaskException.
            _ = Task.Run(async () =>
            {
                try
                {
                    await definition.PostStartAsync(client, _projectRoot, lsSettings, warmupCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (warmupCts.IsCancellationRequested)
                {
                    _logger.LogInformation("Warmup cancelled for {Language} (Stop/Restart)", language);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background warmup for {Language} failed", language);
                    MarkFailed(language);
                }
            }, warmupCts.Token);

            _clients[language] = client;
            EnsureSymbolCache(language);
            _logger.LogInformation("Started language server for {Language} (warmup detached to background)", language);
            return client;
        }
        finally
        {
            gate.Release();
        }
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
        // Cancel any in-flight background warmup first so PostStartAsync
        // unwinds before we tear down the client it's writing to.
        if (_languageCts.TryRemove(language, out var warmupCts))
        {
            try { warmupCts.Cancel(); } catch { /* best-effort */ }
            warmupCts.Dispose();
        }

        if (_caches.TryGetValue(language, out var cache))
        {
            cache.Save();
        }

        if (_clients.Remove(language, out var client))
        {
            await client.StopAsync(ct);
            _logger.LogInformation("Stopped language server for {Language}", language);
        }
        _readyStates.TryRemove(language, out _);
    }

    /// <summary>
    /// Stops a specific language server and removes it from the cache so the
    /// next <see cref="GetOrStartAsync"/> call spawns a fresh process. Use when
    /// the workspace scope or other immutable startup-time options have changed
    /// (e.g., after <c>set_active_solution</c>). Falls back to <see cref="LspClient.ForceKill"/>
    /// when graceful shutdown does not return promptly. Symbol caches are flushed
    /// to disk so they survive across the restart.
    /// </summary>
    public async Task RestartAsync(Language language, CancellationToken ct = default)
    {
        // Cancel background warmup before we tear down its client.
        if (_languageCts.TryRemove(language, out var warmupCts))
        {
            try { warmupCts.Cancel(); } catch { /* best-effort */ }
            warmupCts.Dispose();
        }

        if (!_clients.TryGetValue(language, out var client))
        {
            _logger.LogDebug("No running {Language} server to restart", language);
            return;
        }

        if (_caches.TryGetValue(language, out var cache))
        {
            cache.Save();
        }

        try
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shutdownCts.CancelAfter(TimeSpan.FromSeconds(10));
            await client.StopAsync(shutdownCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graceful shutdown of {Language} failed; force-killing", language);
            try { client.ForceKill(); }
            catch (Exception killEx) { _logger.LogWarning(killEx, "Force-kill of {Language} also failed", language); }
        }

        _clients.Remove(language);
        _readyStates.TryRemove(language, out _);
        _logger.LogInformation("Restarted language server slot for {Language} (cache evicted)", language);
    }

    // === IServerReadyStateSink ===========================================

    /// <inheritdoc />
    public void MarkLoading(Language language, int? projectsTotal = null, string? scopeDescription = null)
    {
        _readyStates[language] = new ReadyStateRecord
        {
            State = WorkspaceReadyState.Loading,
            ProjectsTotal = projectsTotal,
            ScopeDescription = scopeDescription,
            StartedAtTicks = Stopwatch.GetTimestamp(),
        };
    }

    /// <inheritdoc />
    public void MarkReady(Language language)
    {
        _readyStates.AddOrUpdate(
            language,
            _ => new ReadyStateRecord { State = WorkspaceReadyState.Ready, StartedAtTicks = Stopwatch.GetTimestamp() },
            (_, existing) => { existing.State = WorkspaceReadyState.Ready; return existing; });
    }

    /// <inheritdoc />
    public void MarkFailed(Language language)
    {
        _readyStates.AddOrUpdate(
            language,
            _ => new ReadyStateRecord { State = WorkspaceReadyState.Failed, StartedAtTicks = Stopwatch.GetTimestamp() },
            (_, existing) => { existing.State = WorkspaceReadyState.Failed; return existing; });
    }

    /// <inheritdoc />
    public ReadyStateSnapshot GetReadyState(Language language)
    {
        if (!_readyStates.TryGetValue(language, out var record))
        {
            return new ReadyStateSnapshot(WorkspaceReadyState.NotStarted);
        }
        double elapsed = record.StartedAtTicks == 0
            ? 0
            : (Stopwatch.GetTimestamp() - record.StartedAtTicks) / (double)Stopwatch.Frequency;
        return new ReadyStateSnapshot(
            record.State,
            record.ProjectsLoaded,
            record.ProjectsTotal,
            elapsed,
            record.ScopeDescription);
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
