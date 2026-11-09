// SerenaAgent Orchestrator - Phase 7A
// Central agent that manages project activation, tool registration, LS lifecycle

using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Serena.Core.Config;
using Serena.Core.Hooks;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Protocol;

namespace Serena.Core.Agent;

/// <summary>
/// The main Serena agent orchestrator.
/// Manages project activation, language servers, tool registry, and context/modes.
/// Ported from serena/agent.py SerenaAgent.
/// </summary>
public sealed class SerenaAgent : IAsyncDisposable
{
    private readonly ILogger<SerenaAgent> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SerenaConfig _config;
    private readonly LanguageServerRegistry _lsRegistry;
    private ToolRegistry? _toolRegistry;

    private SerenaProject? _activeProject;
    private Project.LanguageServerManager? _lsManager;
    private SerenaAgentMode? _activeMode;
    private readonly List<IPreToolUseHook> _hooks = [];
    private readonly SemaphoreSlim _projectLock = new(1, 1);
    private Project.ProjectFileWatcher? _fileWatcher;

    public SerenaAgent(
        SerenaConfig config,
        LanguageServerRegistry lsRegistry,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _lsRegistry = lsRegistry;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<SerenaAgent>();
    }

    /// <summary>
    /// Currently active project, or null.
    /// </summary>
    public SerenaProject? ActiveProject => _activeProject;

    /// <summary>
    /// The configuration for this agent.
    /// </summary>
    public SerenaConfig Config => _config;

    /// <summary>
    /// The tool registry containing all available tools.
    /// </summary>
    public ToolRegistry Tools =>
        _toolRegistry ?? throw new InvalidOperationException("Tool registry has not been set. Call SetToolRegistry first.");

    /// <summary>
    /// Sets the tool registry. Called after construction to break the circular dependency
    /// between agent and tools (tools need the agent via IToolContext).
    /// </summary>
    public void SetToolRegistry(ToolRegistry registry)
    {
        Volatile.Write(ref _toolRegistry, registry);
    }

    /// <summary>
    /// Active agent mode (controls tool visibility).
    /// </summary>
    public SerenaAgentMode? ActiveMode => _activeMode;

    /// <summary>
    /// Gets all registered project names.
    /// </summary>
    public IReadOnlyList<string> RegisteredProjects =>
        _config.RegisteredProjects.Keys.ToList();

    /// <summary>
    /// Gets the names of languages for which a language server is currently running.
    /// </summary>
    public IReadOnlyList<string> ActiveLanguageServers =>
        _lsManager?.RunningClients.Keys.Select(l => l.ToString()).ToList()
        ?? [];

    /// <summary>
    /// Activates a project by name or path.
    /// </summary>
    public async Task ActivateProjectAsync(string nameOrPath, CancellationToken ct = default)
    {
        var result = await TryActivateProjectAsync(nameOrPath, ct);
        if (result.IsFailure)
        {
            throw new DirectoryNotFoundException(result.Error);
        }
    }

    /// <summary>
    /// Activates a project temporarily and returns a context that restores the previous project on disposal.
    /// </summary>
    /// <example>
    /// await using var ctx = await agent.ActivateProjectTemporarilyAsync("other-project");
    /// </example>
    public async Task<ActiveProjectContext> ActivateProjectTemporarilyAsync(
        string nameOrPath, CancellationToken ct = default)
    {
        var previousProject = _activeProject;
        var previousLsManager = _lsManager;

        var result = await TryActivateProjectAsync(nameOrPath, ct);
        if (result.IsFailure)
        {
            throw new DirectoryNotFoundException(result.Error);
        }

        return new ActiveProjectContext(this, previousProject, previousLsManager);
    }

    /// <summary>
    /// Disposable context that restores the previous active project when disposed.
    /// </summary>
    public sealed class ActiveProjectContext : IAsyncDisposable
    {
        private readonly SerenaAgent _agent;
        private readonly SerenaProject? _previousProject;
        private readonly Project.LanguageServerManager? _previousLsManager;
        private bool _disposed;

        internal ActiveProjectContext(
            SerenaAgent agent,
            SerenaProject? previousProject,
            Project.LanguageServerManager? previousLsManager)
        {
            _agent = agent;
            _previousProject = previousProject;
            _previousLsManager = previousLsManager;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            try
            {
                // Dispose the temporarily-activated language server manager.
                if (_agent._lsManager is not null)
                {
                    await _agent._lsManager.DisposeAsync();
                }
            }
            catch (Exception ex)
            {
                _agent._logger.LogWarning(ex, "Failed to dispose temporary LS manager during project context restore");
            }
            finally
            {
                // Always restore the previous state, even if disposal failed.
                _agent._activeProject = _previousProject;
                _agent._lsManager = _previousLsManager;
                _agent.RestoreFileWatcher();
            }
        }
    }

    /// <summary>
    /// Activates a project by name or path, returning a Result instead of throwing.
    /// </summary>
    public async Task<Result> TryActivateProjectAsync(string nameOrPath, CancellationToken ct = default)
    {
        await _projectLock.WaitAsync(ct);
        try
        {
            RegisteredProject? projectConfig = null;
            if (_config.RegisteredProjects.TryGetValue(nameOrPath, out var found))
            {
                projectConfig = found;
            }
            else
            {
                projectConfig = _config.RegisteredProjects.Values.FirstOrDefault(
                    p => string.Equals(p.Path, nameOrPath, StringComparison.OrdinalIgnoreCase));
            }

            string projectRoot = projectConfig?.Path ?? nameOrPath;
            if (!Directory.Exists(projectRoot))
            {
                return Result.Failure($"Project path not found: {projectRoot}");
            }

            // Activating the project that is already active is a reconcile, not a
            // restart: the caches, the pending outlines and a running Roslyn stay
            // as they are. A sub-agent activating the project its parent works in
            // must not reset the parent's state.
            if (_activeProject is not null && _lsManager is not null && IsSameRoot(_activeProject.Root, projectRoot))
            {
                IndexReconciliations = ReconcileIndexesWithDisk(_activeProject, _lsManager);
                if (_fileWatcher is null)
                {
                    StartFileWatcher(_activeProject);
                }
                _logger.LogInformation("Project already active, index reconciled: {Path}", projectRoot);
                return Result.Success();
            }

            StopFileWatcher();
            if (_lsManager is not null)
            {
                await _lsManager.DisposeAsync();
            }

            _activeProject = new SerenaProject(projectRoot, _loggerFactory.CreateLogger<SerenaProject>(), projectConfig);
            _lsManager = new Project.LanguageServerManager(
                projectRoot,
                _lsRegistry,
                _loggerFactory);

            _logger.LogInformation("Activated project: {Path}", projectRoot);
            IndexReconciliations = ReconcileIndexesWithDisk(_activeProject, _lsManager);
            StartFileWatcher(_activeProject);
            return Result.Success();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    private static bool IsSameRoot(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What activation did to each language's on-disk index: files deleted
    /// since the index was built are dropped, files changed or created by
    /// other tools get fresh occurrence rows. Empty when no index exists.
    /// </summary>
    public IReadOnlyDictionary<Language, CacheReconciliation> IndexReconciliations { get; private set; } =
        new Dictionary<Language, CacheReconciliation>();

    /// <summary>
    /// Files are gathered only when at least one language has an index, so a
    /// project that was never indexed pays nothing at activation.
    /// </summary>
    private IReadOnlyDictionary<Language, CacheReconciliation> ReconcileIndexesWithDisk(
        SerenaProject project, Project.LanguageServerManager lsManager)
    {
        var results = new Dictionary<Language, CacheReconciliation>();
        try
        {
            var languages = project.GetConfiguredLanguages() is { Count: > 0 } configured
                ? configured
                : [Language.CSharp, Language.TypeScript];
            var indexed = languages.Where(l => lsManager.GetOrLoadSymbolCache(l) is not null).ToList();
            if (indexed.Count == 0)
            {
                return results;
            }

            var grouped = Project.ProjectIndexer.GroupFilesByLanguage(project.GatherSourceFiles(), project);
            foreach (var language in indexed)
            {
                var files = grouped.GetValueOrDefault(language) ?? [];
                var result = lsManager.ReconcileSymbolCache(
                    language, files.Select(file => Path.Combine(project.Root, file)));
                if (result is not null)
                {
                    results[language] = result;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Index reconciliation at activation failed; the index is used as is");
        }
        return results;
    }

    /// <summary>
    /// Watches the active project for files written by anything other than
    /// serena's own tools, so they reach the index as they land instead of at
    /// the next activation. Only a project with an index has one.
    /// </summary>
    private void StartFileWatcher(SerenaProject project)
    {
        if (IndexReconciliations.Count == 0)
        {
            return;
        }
        try
        {
            _fileWatcher = new Project.ProjectFileWatcher(
                project,
                () => _lsManager,
                () => Task.Run(() =>
                {
                    if (_lsManager is { } manager)
                    {
                        ReconcileIndexesWithDisk(project, manager);
                    }
                }),
                IndexReconciliations.Keys,
                _loggerFactory.CreateLogger<Project.ProjectFileWatcher>());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not watch {Root} for external changes; the index refreshes at the next activation", project.Root);
        }
    }

    private void StopFileWatcher()
    {
        _fileWatcher?.Dispose();
        _fileWatcher = null;
    }

    private void RestoreFileWatcher()
    {
        StopFileWatcher();
        if (_activeProject is not null && _lsManager is not null)
        {
            IndexReconciliations = ReconcileIndexesWithDisk(_activeProject, _lsManager);
            StartFileWatcher(_activeProject);
        }
    }

    internal Project.ProjectFileWatcher? FileWatcher => _fileWatcher;

    internal Project.LanguageServerManager? LanguageServerManager => _lsManager;

    /// <summary>
    /// Gets or starts a language server for a given file.
    /// </summary>
    public async Task<Lsp.Client.LspClient?> GetLanguageServerForFileAsync(
        string filePath,
        CancellationToken ct = default)
    {
        if (_lsManager is null)
        {
            return null;
        }

        string ext = Path.GetExtension(filePath);
        var language = LanguageExtensions.FromFileExtension(ext);
        if (language is null)
        {
            return null;
        }
        if (_activeProject is not null && !_activeProject.IsLanguageEnabled(language.Value))
        {
            _logger.LogDebug(
                "Language {Language} is not enabled for active project {Project}",
                language.Value,
                _activeProject.Root);
            return null;
        }

        var settings = BuildLsSettings(language.Value);
        return await _lsManager.GetOrStartAsync(language.Value, settings, ct: ct);
    }

    /// <summary>
    /// The language server already running for a file's language, or null.
    /// Never starts a server, so edit tools can report diagnostics when Roslyn
    /// happens to be warm without paying a cold start when it is not.
    /// </summary>
    public Lsp.Client.LspClient? TryGetRunningLanguageServer(string filePath)
    {
        if (_lsManager is null)
        {
            return null;
        }

        var language = LanguageExtensions.FromFileExtension(Path.GetExtension(filePath));
        if (language is null)
        {
            return null;
        }
        if (_activeProject is not null && !_activeProject.IsLanguageEnabled(language.Value))
        {
            return null;
        }

        return _lsManager.TryGetRunningClient(language.Value);
    }

    /// <summary>
    /// Builds language-server-specific settings, injecting per-project context
    /// (e.g., C# solution scope from <c>csharp.scope.solutions</c> in project.yml
    /// or the <c>set_active_solution</c> tool).
    /// </summary>
    private CustomLsSettings? BuildLsSettings(Language language)
    {
        if (_activeProject is null)
        {
            return null;
        }

        var dict = new Dictionary<string, object>(
            _activeProject.GetLanguageServerSettings(language),
            StringComparer.OrdinalIgnoreCase);

        if (language == Language.CSharp)
        {
            var scope = _activeProject.GetCSharpScope();
            if (!scope.IsEmpty)
            {
                dict[CSharpLanguageServer.ScopeSolutionsSetting] =
                    string.Join(';', scope.SolutionPaths);
            }
        }

        return dict.Count == 0 ? null : new CustomLsSettings(dict);
    }

    /// <summary>
    /// Restarts the running language server for the given language with the
    /// project's current startup-time settings (notably the C# solution scope),
    /// so it comes back Loading and warms up in the background. No-op when the
    /// server isn't running.
    /// </summary>
    public Task RestartLanguageServerAsync(Language language, CancellationToken ct = default)
    {
        if (_lsManager is null)
        {
            return Task.CompletedTask;
        }
        return _lsManager.RestartAsync(language, BuildLsSettings(language), ct);
    }

    /// <summary>Files whose outline an edit dropped and the server has not rebuilt yet.</summary>
    public int PendingOutlineCount(Language language) => _lsManager?.PendingOutlineCount(language) ?? 0;

    /// <summary>Why the last background outline rebuild failed, or null.</summary>
    public string? LastOutlineFailure(Language language) => _lsManager?.LastOutlineFailure(language);

    /// <summary>
    /// Returns the workspace-load readiness state for the given language.
    /// Returns a NotStarted snapshot if no project is active or the server
    /// has not been started. Callers (notably symbol tools) use this to
    /// translate timeout-on-warmup events into actionable status responses.
    /// </summary>
    public ReadyStateSnapshot GetLanguageServerReadyState(Language language) =>
        _lsManager?.GetReadyState(language) ?? new ReadyStateSnapshot(WorkspaceReadyState.NotStarted);

    /// <summary>
    /// v1.0.27: Eagerly starts the language server for <paramref name="language"/>
    /// and returns the immediate readiness snapshot. Workspace warmup
    /// (Roslyn solution/open + indexing) continues in the background after
    /// this method returns; callers should poll <see cref="GetLanguageServerReadyState"/>
    /// until <see cref="WorkspaceReadyState.Ready"/>. No-op (returns NotStarted)
    /// when no project is active.
    /// </summary>
    public async Task<ReadyStateSnapshot> WarmLanguageServerAsync(Language language, CancellationToken ct = default)
    {
        if (_lsManager is null)
        {
            return new ReadyStateSnapshot(WorkspaceReadyState.NotStarted);
        }
        var settings = BuildLsSettings(language);
        await _lsManager.GetOrStartAsync(language, settings, ct).ConfigureAwait(false);
        return _lsManager.GetReadyState(language);
    }

    /// <summary>
    /// Gets the symbol cache for the specified language. Loads from disk on
    /// demand WITHOUT starting a language server, so callers can serve from
    /// cache before paying Roslyn startup cost on large repos. Returns null
    /// when no cache file exists on disk.
    /// </summary>
    public SymbolCache<UnifiedSymbolInformation[]>? GetSymbolCache(Language language) =>
        _lsManager?.GetOrLoadSymbolCache(language);

    /// <summary>
    /// Records a file write without starting a language server. The active
    /// project's symbol cache drops the file's stale outline immediately,
    /// rebuilds it only when the matching language server is already warm,
    /// and rebuilds the occurrence rows from the text either way. Null when
    /// the file is not source of an enabled language or no project is active.
    /// </summary>
    public async Task<SymbolCacheUpdate?> UpdateSymbolCacheForWrittenFileAsync(
        string absolutePath,
        string content,
        CancellationToken ct = default)
    {
        if (_lsManager is null)
        {
            return null;
        }

        var language = LanguageExtensions.FromFileExtension(Path.GetExtension(absolutePath));
        if (language is null)
        {
            return null;
        }

        if (_activeProject is not null && !_activeProject.IsLanguageEnabled(language.Value))
        {
            return null;
        }

        return await _lsManager.UpdateSymbolCacheForWrittenFileAsync(language.Value, absolutePath, content, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Project root for the active project, or null if none activated.
    /// </summary>
    public string? ActiveProjectRoot => _activeProject?.Root;

    /// <summary>
    /// Sets the active mode.
    /// </summary>
    public void SetMode(SerenaAgentMode mode)
    {
        _activeMode = mode;
        _logger.LogInformation("Set mode to: {Mode}", mode.Name);
    }

    /// <summary>
    /// Registers a pre-tool-use hook that will be evaluated before every tool execution.
    /// </summary>
    public void RegisterHook(IPreToolUseHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        _hooks.Add(hook);
    }

    /// <summary>
    /// Runs all registered pre-tool-use hooks and returns the combined result.
    /// If any hook returns <see cref="HookResult.Deny"/>, execution is denied.
    /// Messages from <see cref="HookResult.AllowWithMessage"/> hooks are collected and concatenated.
    /// </summary>
    public async Task<(HookResult Result, string? Message)> RunPreToolHooksAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct = default)
    {
        List<string>? messages = null;

        foreach (var hook in _hooks)
        {
            var (result, message) = await hook.OnBeforeToolUseAsync(toolName, parameters, ct);

            if (result == HookResult.Deny)
            {
                return (HookResult.Deny, message ?? "Tool execution denied by hook.");
            }

            if (result == HookResult.AllowWithMessage && !string.IsNullOrEmpty(message))
            {
                messages ??= [];
                messages.Add(message);
            }
        }

        if (messages is { Count: > 0 })
        {
            return (HookResult.AllowWithMessage, string.Join("\n", messages));
        }

        return (HookResult.Allow, null);
    }

    /// <summary>
    /// Gets tool descriptions suitable for MCP tool listing.
    /// </summary>
    public IReadOnlyList<ToolDescription> GetToolDescriptions()
    {
        return Tools.All
            .Select(t =>
            {
                bool requiresProject = t is ToolBase tb && tb.RequiresActiveProject;
                if (requiresProject && _activeProject is null)
                {
                    return null;
                }
                return new ToolDescription(t.Name, t.Description, t.Parameters);
            })
            .Where(t => t is not null)
            .Cast<ToolDescription>()
            .ToList();
    }

    /// <summary>
    /// Disposes the current <see cref="LanguageServerManager"/> and creates a fresh one,
    /// allowing recovery from hung or broken language servers.
    /// </summary>
    public async Task ResetLanguageServerManagerAsync()
    {
        await _projectLock.WaitAsync();
        try
        {
            var runningLanguages = _lsManager?.RunningClients.Keys.ToList() ?? [];
            if (_lsManager is not null)
            {
                await _lsManager.DisposeAsync();
            }

            if (_activeProject is not null)
            {
                _lsManager = new Project.LanguageServerManager(
                    _activeProject.Root,
                    _lsRegistry,
                    _loggerFactory);
                _logger.LogInformation("Language server manager has been reset");

                // A restart that leaves the server NotStarted makes every later edit
                // skip its outline refresh until something else starts the server.
                // Start again what was running; warmup continues in the background.
                foreach (var language in runningLanguages)
                {
                    try
                    {
                        await _lsManager.GetOrStartAsync(language, BuildLsSettings(language)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to start {Language} language server after reset", language);
                    }
                }
            }
            else
            {
                _lsManager = null;
                _logger.LogWarning("No active project; language server manager cleared");
            }
        }
        finally
        {
            _projectLock.Release();
        }
    }

    /// <summary>
    /// Force-kills the language server processes WITHOUT graceful shutdown.
    /// Use when a tool is cancelled and Roslyn is burning CPU — graceful
    /// shutdown can wait 10+ seconds for an unresponsive server.
    /// </summary>
    public async Task ForceResetLanguageServerManagerAsync()
    {
        await _projectLock.WaitAsync();
        try
        {
            _lsManager?.ForceKillAll();

            if (_activeProject is not null)
            {
                _lsManager = new Project.LanguageServerManager(
                    _activeProject.Root,
                    _lsRegistry,
                    _loggerFactory);
                _logger.LogWarning("Language server manager has been force-reset");
            }
            else
            {
                _lsManager = null;
            }
        }
        finally
        {
            _projectLock.Release();
        }
    }

    /// <summary>
    /// Creates a system prompt describing the current agent context
    /// (active project, tools, memories, mode) for LLM consumption.
    /// </summary>
    public string CreateSystemPrompt() =>
        Templates.SystemPromptFactory.CreateSystemPrompt(this);

    public async ValueTask DisposeAsync()
    {
        StopFileWatcher();
        if (_lsManager is not null)
        {
            await _lsManager.DisposeAsync();
        }
    }
}

/// <summary>
/// Description of a tool for MCP registration.
/// </summary>
public sealed record ToolDescription(
    string Name,
    string Description,
    IReadOnlyList<ToolParameter> Parameters);
