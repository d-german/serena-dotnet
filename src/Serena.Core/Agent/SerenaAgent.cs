// SerenaAgent Orchestrator - Phase 7A
// Central agent that manages project activation, tool registration, LS lifecycle

using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp;
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
        _toolRegistry = registry;
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
    /// Activates a project by name or path, returning a Result instead of throwing.
    /// </summary>
    public async Task<Result> TryActivateProjectAsync(string nameOrPath, CancellationToken ct = default)
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
    /// Sets the active mode.
    /// </summary>
    public void SetMode(SerenaAgentMode mode)
    {
        _activeMode = mode;
        _logger.LogInformation("Set mode to: {Mode}", mode.Name);
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
