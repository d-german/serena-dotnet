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
            if (_lsManager is not null)
            {
                await _lsManager.DisposeAsync();
            }

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

        _activeProject = new SerenaProject(projectRoot, _loggerFactory.CreateLogger<SerenaProject>(), projectConfig);
        _lsManager = new Project.LanguageServerManager(
            projectRoot,
            _lsRegistry,
            _loggerFactory);

        _logger.LogInformation("Activated project: {Path}", projectRoot);
            return Result.Success();
        }
        finally
        {
            _projectLock.Release();
        }
    }

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

        return await _lsManager.GetOrStartAsync(language.Value, ct: ct);
    }

    /// <summary>
    /// Gets the symbol cache for the specified language, or null if unavailable.
    /// </summary>
    public SymbolCache<UnifiedSymbolInformation[]>? GetSymbolCacheForLanguage(Language language) =>
        _lsManager?.GetSymbolCache(language);

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
    /// Creates a system prompt describing the current agent context
    /// (active project, tools, memories, mode) for LLM consumption.
    /// </summary>
    public string CreateSystemPrompt() =>
        Templates.SystemPromptFactory.CreateSystemPrompt(this);

    public async ValueTask DisposeAsync()
    {
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
