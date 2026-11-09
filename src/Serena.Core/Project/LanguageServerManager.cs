// Language Server Manager - Phase 5D
// Manages language server lifecycle per project

using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Outline;
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
    // 2: C# outlines merge in .NET Framework-only declarations and wait for DEBUG, so
    // caches built before then must be rebuilt rather than reused by fingerprint.
    internal const int SymbolCacheVersion = 2;

    // Write-behind delay for the symbol caches this manager owns: an edit's
    // index update reaches disk this long after the last mutation, so a session
    // that ends without a graceful shutdown loses at most this window.
    internal static readonly TimeSpan SymbolCacheSaveDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger<LanguageServerManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly LanguageServerRegistry _registry;
    private readonly string _projectRoot;
    private readonly Dictionary<Language, LspClient> _clients = [];
    // Read by tool calls and, since 0.7.1, by the project file watcher on its own thread.
    private readonly ConcurrentDictionary<Language, SymbolCache<UnifiedSymbolInformation[]>> _caches = new();
    private readonly object _cacheGate = new();
    private readonly ConcurrentDictionary<Language, ReadyStateRecord> _readyStates = new();
    // v1.0.27: per-language CTS owns the lifetime of background warmup
    // (PostStartAsync). Cancelled + disposed in StopAsync/RestartAsync so a
    // restart can interrupt an in-flight Roslyn load.
    private readonly ConcurrentDictionary<Language, CancellationTokenSource> _languageCts = new();
    private readonly ConcurrentDictionary<Language, byte> _tooLargeToLoad = new();
    // v1.0.27: per-language gate so two parallel first-callers don't both
    // race past the TryGetValue check and start two LSP processes.
    private readonly ConcurrentDictionary<Language, SemaphoreSlim> _startGates = new();
    // The settings each server was last started with, so a restart can start
    // it again the same way without the caller repeating them.
    private readonly ConcurrentDictionary<Language, CustomLsSettings?> _lastSettings = new();

    // Files whose outline was dropped by an edit and not yet rebuilt, with the number of
    // rebuild attempts so far. Drained in the background whenever the server is usable.
    private readonly ConcurrentDictionary<Language, ConcurrentDictionary<string, int>> _pendingOutlines = new();
    private readonly ConcurrentDictionary<Language, SemaphoreSlim> _drainGates = new();
    // The most recent reason an outline rebuild failed, so a status query can show it; the
    // server's own log is stderr, which an MCP host usually discards.
    private readonly ConcurrentDictionary<Language, string> _lastOutlineFailure = new();
    private readonly CancellationTokenSource _lifetime = new();

    /// <summary>A file whose outline fails this many times in a row is given up on until the next index.</summary>
    internal const int MaxOutlineAttempts = 3;

    /// <summary>
    /// The background rebuild is off the agent's request path, so it can wait for a
    /// loaded Roslyn far longer than the inline stale scan.
    /// </summary>
    internal static readonly TimeSpan OutlineRefreshTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Mutable internal record so MarkLoading/MarkReady can update a single
    /// instance without rebuilding it. <see cref="GetReadyState"/> returns an
    /// immutable <see cref="ReadyStateSnapshot"/> derived from this record.
    /// </summary>
    private sealed class ReadyStateRecord
    {
        public WorkspaceReadyState State;
        public int? ProjectsLoaded;
        public int? ProjectsTotal;
        public string? ScopeDescription;
        public IReadOnlyList<string>? Warnings;
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

            _lastSettings[language] = settings;
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
                catch (UnscopedRepositoryTooLargeException ex)
                {
                    // Expected on large monorepos and repeated on every restart during
                    // batch indexing, so warn once without a stack trace.
                    if (_tooLargeToLoad.TryAdd(language, 0))
                    {
                        _logger.LogWarning("{Language} workspace not loaded: {Reason}", language, ex.Message);
                    }
                    MarkFailed(language, ex.Message);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background warmup for {Language} failed", language);
                    MarkFailed(language, ex.Message);
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
        EnsureSymbolCache(language);
        var cache = _caches[language];
        // EnsureSymbolCache always inserts an entry but Load() may have returned
        // false because no cache file existed at the time. The file can appear
        // later (a `project index` run in another process), so an empty cache
        // tries to load again before being reported as absent, and null means
        // the caller can fall through to LSP without thinking the cache is
        // empty-but-real.
        if (cache.Count == 0 && !cache.Load())
        {
            return null;
        }
        return cache;
    }

    /// <summary>
    /// Records that a source file was changed by an edit tool without starting
    /// a language server. The stale outline is dropped at once so cache-backed
    /// searches never return old symbols, the occurrence rows are rebuilt from
    /// the text at once, and the outline is queued for a background rebuild:
    /// started now when the server is running and Ready or Partial, deferred
    /// until it is otherwise. The cache persists every change itself
    /// (write-behind). The edit tool returns without waiting for Roslyn.
    /// </summary>
    public async Task<SymbolCacheUpdate> UpdateSymbolCacheForWrittenFileAsync(
        Language language,
        string absolutePath,
        string content,
        CancellationToken ct = default)
    {
        EnsureSymbolCache(language);
        var cache = _caches[language];
        var state = GetReadyState(language).State;

        if (_clients.TryGetValue(language, out var client) && client.IsRunning)
        {
            await TryNotifyRunningLanguageServerAsync(client, absolutePath, content).ConfigureAwait(false);
        }

        // C# needs no server for its outline: the parser rebuilds it from the written text now.
        if (language == Language.CSharp && File.Exists(absolutePath))
        {
            string fingerprint = CacheFingerprint.ForFile(absolutePath);
            cache.Set(absolutePath, fingerprint, [.. CSharpSyntaxOutline.Build(absolutePath, content)]);
            return new SymbolCacheUpdate(OutlineRefresh.Refreshed, true, state);
        }

        cache.Remove(absolutePath);
        bool occurrencesRefreshed = cache.Occurrences.Capture(absolutePath);

        if (!File.Exists(absolutePath))
        {
            return new SymbolCacheUpdate(OutlineRefresh.Started, occurrencesRefreshed, state);
        }

        PendingOutlines(language)[SymbolCacheKeys.Normalize(absolutePath)] = 0;

        if (client is { IsRunning: true } && CanRefreshInline(state))
        {
            StartOutlineRefresh(language);
            return new SymbolCacheUpdate(OutlineRefresh.Started, occurrencesRefreshed, state);
        }

        return new SymbolCacheUpdate(OutlineRefresh.Deferred, occurrencesRefreshed, state);
    }

    /// <summary>
    /// The cache for a language, created and loaded on first use. Unlike
    /// <see cref="GetOrLoadSymbolCache"/> it never returns null, so an indexer
    /// that builds outlines without a server has somewhere to put them.
    /// </summary>
    public SymbolCache<UnifiedSymbolInformation[]> GetOrCreateSymbolCache(Language language)
    {
        EnsureSymbolCache(language);
        return _caches[language];
    }

    /// <summary>Files whose outline is waiting to be rebuilt for a language.</summary>
    public int PendingOutlineCount(Language language) =>
        _pendingOutlines.TryGetValue(language, out var pending) ? pending.Count : 0;

    /// <summary>Why the last outline rebuild for a language failed, with its time and file; null when none has.</summary>
    public string? LastOutlineFailure(Language language) =>
        _lastOutlineFailure.TryGetValue(language, out var reason) ? reason : null;

    private ConcurrentDictionary<string, int> PendingOutlines(Language language) =>
        _pendingOutlines.GetOrAdd(language, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Kicks off a background drain of the pending outlines when a usable server
    /// is running. Fire-and-forget; a drain already in progress is left alone,
    /// and it re-checks the pending set before finishing, so a file queued while
    /// it ran is not missed.
    /// </summary>
    private void StartOutlineRefresh(Language language)
    {
        if (PendingOutlineCount(language) == 0
            || !_clients.TryGetValue(language, out var client) || !client.IsRunning
            || !CanRefreshInline(GetReadyState(language).State)
            || !_caches.TryGetValue(language, out var cache))
        {
            return;
        }

        var refresher = SymbolCacheRefresher.ForLspClient(_projectRoot, cache, client, _logger, OutlineRefreshTimeout);
        _ = Task.Run(async () =>
        {
            try
            {
                await DrainPendingOutlinesAsync(language, refresher.RefreshFileDetailedAsync, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background outline refresh for {Language} failed", language);
            }
        }, _lifetime.Token);
    }

    /// <summary>
    /// Rebuilds the outline of every pending file with <paramref name="reindex"/>,
    /// one language at a time. A file that changed while its outline was being
    /// requested is dropped from the cache again and stays pending; a file that
    /// fails <see cref="MaxOutlineAttempts"/> times is given up on with a warning.
    /// Returns the number of outlines rebuilt.
    /// </summary>
    internal async Task<int> DrainPendingOutlinesAsync(
        Language language,
        ReindexFileDetailedDelegate reindex,
        CancellationToken ct)
    {
        var gate = _drainGates.GetOrAdd(language, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return 0;
        }

        int rebuilt = 0;
        try
        {
            var pending = PendingOutlines(language);
            EnsureSymbolCache(language);
            var cache = _caches[language];

            // Files queued while this pass runs are picked up by the next pass of the loop.
            while (!ct.IsCancellationRequested)
            {
                var batch = pending.Keys.ToList();
                if (batch.Count == 0)
                {
                    break;
                }
                foreach (string path in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(path))
                    {
                        cache.Remove(path);
                        pending.TryRemove(path, out _);
                        continue;
                    }

                    string before = CacheFingerprint.ForFile(path);
                    var outcome = await reindex(path, ct).ConfigureAwait(false);
                    if (outcome.Updated)
                    {
                        if (CacheFingerprint.ForFile(path) != before)
                        {
                            // Edited again while Roslyn was answering: the outline we stored
                            // describes the old text. Drop it and try again on the next pass.
                            cache.Remove(path);
                            cache.Occurrences.Capture(path);
                            continue;
                        }
                        pending.TryRemove(path, out _);
                        rebuilt++;
                        continue;
                    }

                    int attempts = pending.AddOrUpdate(path, 1, (_, previous) => previous + 1);
                    _lastOutlineFailure[language] =
                        $"{DateTimeOffset.Now:HH:mm:ss} {Path.GetFileName(path)}: {outcome.Reason ?? "no reason given"} (attempt {attempts} of {MaxOutlineAttempts})";
                    if (attempts >= MaxOutlineAttempts)
                    {
                        pending.TryRemove(path, out _);
                        _logger.LogWarning(
                            "Outline for {Path} not rebuilt after {Attempts} attempts ({Reason}); it returns with the next index",
                            path, attempts, outcome.Reason ?? "no reason given");
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Outline for {Path} not rebuilt ({Reason}); attempt {Attempt} of {Max}, retrying when the server is next usable",
                            path, outcome.Reason ?? "no reason given", attempts, MaxOutlineAttempts);
                    }
                }

                // Everything left failed this pass; wait for the next trigger rather than spinning.
                if (pending.Keys.All(key => pending.TryGetValue(key, out int tries) && tries > 0))
                {
                    break;
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (rebuilt > 0)
        {
            _logger.LogInformation("Rebuilt {Count} outline(s) for {Language} in the background", rebuilt, language);
        }
        return rebuilt;
    }

    /// <summary>
    /// Brings a language's on-disk index in line with the files now present
    /// (deleted files dropped, changed and new files re-outlined by the parser
    /// for C#, re-captured from text for the rest) without starting a server.
    /// Null when the language has no index.
    /// </summary>
    public CacheReconciliation? ReconcileSymbolCache(Language language, IEnumerable<string> absolutePaths)
    {
        var cache = GetOrLoadSymbolCache(language);
        if (cache is null)
        {
            return null;
        }
        Func<string, Maybe<UnifiedSymbolInformation[]>>? rebuild = language == Language.CSharp
            ? path => CSharpSyntaxOutline.TryBuild(path) is { } outline
                ? Maybe.From<UnifiedSymbolInformation[]>([.. outline])
                : Maybe<UnifiedSymbolInformation[]>.None
            : null;
        var result = cache.ReconcileWithDisk(absolutePaths, rebuild);
        if (result.Removed > 0 || result.Refreshed > 0)
        {
            _logger.LogInformation(
                "Reconciled {Language} index with disk: {Known} known, {Removed} removed, {Refreshed} refreshed from text",
                language, result.Known, result.Removed, result.Refreshed);
        }
        return result;
    }

    /// <summary>
    /// A single-file reindex only needs the server to answer document symbol
    /// requests, which it does in both Ready and Partial. Gating on Ready alone
    /// left every edited or created file out of the cache for the whole
    /// session on solutions where one project fails to load (Roslyn then ends
    /// Partial every time).
    /// </summary>
    internal static bool CanRefreshInline(WorkspaceReadyState state) =>
        state is WorkspaceReadyState.Ready or WorkspaceReadyState.Partial;

    /// <summary>
    /// The running client for a language, or null. Never starts a server.
    /// </summary>
    public LspClient? TryGetRunningClient(Language language) =>
        _clients.TryGetValue(language, out var client) && client.IsRunning ? client : null;

    private async Task TryNotifyRunningLanguageServerAsync(
        LspClient client,
        string absolutePath,
        string content)
    {
        try
        {
            if (client.FileBuffers.GetBuffer(LspClient.PathToUri(absolutePath)) is null)
            {
                return;
            }
            await client.NotifyFileChangedAsync(absolutePath, content).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort LSP write notification failed for {Path}", absolutePath);
        }
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

        if (_clients.Remove(language, out var client))
        {
            await client.StopAsync(ct);
            _logger.LogInformation("Stopped language server for {Language}", language);
        }
        _readyStates.TryRemove(language, out _);
    }

    /// <summary>
    /// Stops a specific language server and starts it again, with the given
    /// settings or, when none are given, the settings it was last started
    /// with. Use when the workspace scope or other immutable startup-time
    /// options have changed (e.g., after <c>set_active_solution</c>), or when
    /// the server is stuck. Falls back to <see cref="LspClient.ForceKill"/>
    /// when graceful shutdown does not return promptly. The new server comes
    /// back in the Loading state and warms up in the background; no-op when
    /// the server was not running. The symbol cache is kept in memory across
    /// the restart and persists itself.
    /// </summary>
    public async Task RestartAsync(Language language, CustomLsSettings? settings = null, CancellationToken ct = default)
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

        var startSettings = settings ?? _lastSettings.GetValueOrDefault(language);
        await GetOrStartAsync(language, startSettings, ct).ConfigureAwait(false);
        _logger.LogInformation("Restarted language server for {Language}; warming up", language);
    }

    /// <summary>
    /// Kills the language server without the LSP shutdown handshake, so the next
    /// <see cref="GetOrStartAsync"/> starts a fresh process. For batch indexing,
    /// where the server holds nothing worth draining: a graceful
    /// <see cref="RestartAsync"/> waits out the full shutdown timeout whenever the
    /// server ignores "exit", which Roslyn routinely does. The symbol cache is
    /// untouched; the indexer saves it at its own checkpoints.
    /// </summary>
    public async Task RecycleAsync(Language language, CancellationToken ct = default)
    {
        if (_languageCts.TryRemove(language, out var warmupCts))
        {
            try { warmupCts.Cancel(); } catch { /* best-effort */ }
            warmupCts.Dispose();
        }

        _readyStates.TryRemove(language, out _);
        if (!_clients.Remove(language, out var client))
        {
            return;
        }

        // An expected kill marks the process as shutting down, so disposing it right away
        // releases the client, its JSON-RPC connection and the process handle without
        // starting the graceful shutdown this method exists to skip.
        client.ForceKill(expected: true);
        await client.DisposeAsync();
        _logger.LogInformation("Recycled language server for {Language}", language);
    }

    /// <summary>
    /// True once the language server's warmup found the repository too large to open
    /// without a solution scope. Every file is then a miscellaneous document, the case in
    /// which Roslyn slows with each file it has seen and restarts pay for themselves.
    /// </summary>
    public bool IsRepositoryTooLargeToLoad(Language language) => _tooLargeToLoad.ContainsKey(language);

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
    public void MarkReady(
        Language language,
        int? projectsLoaded = null,
        IReadOnlyList<string>? warnings = null)
    {
        _readyStates.AddOrUpdate(
            language,
            _ => new ReadyStateRecord
            {
                State = WorkspaceReadyState.Ready,
                ProjectsLoaded = projectsLoaded,
                Warnings = warnings,
                StartedAtTicks = Stopwatch.GetTimestamp(),
            },
            (_, existing) =>
            {
                existing.State = WorkspaceReadyState.Ready;
                existing.ProjectsLoaded = projectsLoaded ?? existing.ProjectsLoaded;
                existing.Warnings = warnings;
                return existing;
            });
        // The server just became usable: rebuild the outlines edits dropped while it was loading.
        StartOutlineRefresh(language);
    }

    /// <inheritdoc />
    public void MarkPartial(
        Language language,
        string warning,
        int? projectsLoaded = null,
        IReadOnlyList<string>? warnings = null)
    {
        var combinedWarnings = new[] { warning }
            .Concat(warnings ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        _readyStates.AddOrUpdate(
            language,
            _ => new ReadyStateRecord
            {
                State = WorkspaceReadyState.Partial,
                ProjectsLoaded = projectsLoaded,
                Warnings = combinedWarnings,
                StartedAtTicks = Stopwatch.GetTimestamp(),
            },
            (_, existing) =>
            {
                existing.State = WorkspaceReadyState.Partial;
                existing.ProjectsLoaded = projectsLoaded ?? existing.ProjectsLoaded;
                existing.Warnings = combinedWarnings;
                return existing;
            });
        StartOutlineRefresh(language);
    }

    /// <inheritdoc />
    public void MarkFailed(Language language, string? warning = null)
    {
        _readyStates.AddOrUpdate(
            language,
            _ => new ReadyStateRecord
            {
                State = WorkspaceReadyState.Failed,
                Warnings = warning is null ? null : [warning],
                StartedAtTicks = Stopwatch.GetTimestamp()
            },
            (_, existing) =>
            {
                existing.State = WorkspaceReadyState.Failed;
                existing.Warnings = warning is null ? existing.Warnings : [warning];
                return existing;
            });
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
            record.ScopeDescription,
            record.Warnings);
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
        _lifetime.Cancel();
        DisposeCaches();

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
        _lifetime.Cancel();
        DisposeCaches();

        foreach (var (language, client) in _clients.ToList())
        {
            try { client.ForceKill(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error force-killing LS for {Language}", language); }
        }
        _clients.Clear();
    }

    /// <summary>
    /// Returns the per-LSP-request timeout. Single source of truth is
    /// <see cref="LspTimeout.RequestTimeout"/>, which reads
    /// SERENA_LSP_REQUEST_TIMEOUT_SECONDS (clamped 5..1800, default 60s).
    /// Both the per-call wrapper and the underlying JSON-RPC infrastructure
    /// honor the same value so behavior is predictable.
    /// </summary>
    private static TimeSpan GetLspRequestTimeout()
    {
        return Serena.Core.Editor.LspTimeout.RequestTimeout;
    }
    private void EnsureSymbolCache(Language language)
    {
        if (_caches.ContainsKey(language))
        {
            return;
        }

        lock (_cacheGate)
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
                _loggerFactory.CreateLogger<SymbolCache<UnifiedSymbolInformation[]>>(),
                SymbolCacheSaveDelay);
            cache.Load();
            _caches[language] = cache;
        }
    }

    /// <summary>Flushes and releases every cache; disposing a cache saves what is pending.</summary>
    private void DisposeCaches()
    {
        foreach (var (_, cache) in _caches)
        {
            try
            {
                cache.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error saving symbol cache");
            }
        }
        _caches.Clear();
    }
}

/// <summary>
/// What an edit did to the index for one file: whether the outline rebuild was
/// started in the background (the server is running and Ready or Partial) or
/// deferred until a server is, whether the occurrence rows were rebuilt from
/// the text, and the server state at the time, for the line an edit tool shows
/// when symbol queries on the file will have to wait.
/// </summary>
public sealed record SymbolCacheUpdate(OutlineRefresh Outline, bool OccurrencesRefreshed, WorkspaceReadyState ServerState);

public enum OutlineRefresh
{
    /// <summary>The outline was rebuilt from the written text at once (C#, by the parser).</summary>
    Refreshed,

    /// <summary>A usable server is running; the outline is being rebuilt in the background.</summary>
    Started,

    /// <summary>No usable server; the outline is rebuilt when one becomes Ready or Partial.</summary>
    Deferred,
}
