// Serena.Mcp - MCP server entry point
// Exposes Serena tools via Model Context Protocol over stdio

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Mcp;

public static class Program
{
    public static async Task Main(string[] args)
    {
        string? projectToActivate = ParseProjectArg(args);

        // Build all Serena services eagerly (outside DI) to avoid the double-container bug.
        // Previously, BuildServiceProvider() created one container and builder.Build() another,
        // causing MCP tools to reference a different agent than the host lifecycle.
        // MCP over stdio: stdout is reserved for JSON-RPC. All logging must go to stderr.
        var loggerFactory = LoggerFactory.Create(b => b
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning));

        string configPath = Path.Combine(SerenaPaths.Instance.SerenaUserHomeDir, "config.yml");
        var configResult = SerenaConfig.LoadFromFile(configPath, loggerFactory.CreateLogger<SerenaConfig>());
        var config = configResult.IsSuccess ? configResult.Value : new SerenaConfig(loggerFactory.CreateLogger<SerenaConfig>());

        var lsRegistry = new LanguageServerRegistry();
        BuiltInLanguageServers.RegisterAll(lsRegistry, loggerFactory);
        AdditionalLanguageServers.RegisterAll(lsRegistry, loggerFactory);

        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var toolRegistry = BuildToolRegistry(agent, loggerFactory);
        agent.SetToolRegistry(toolRegistry);

        // Configure the host — inject pre-built singletons so there's ONE instance of each
        var builder = Host.CreateApplicationBuilder(args);
        // Route all host logging to stderr so it doesn't corrupt the stdio JSON-RPC stream
        builder.Logging.ClearProviders()
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools(SerenaMcpToolBridge.CreateMcpTools(toolRegistry, agent, loggerFactory));

        var app = builder.Build();

        await ActivateStartupProjectAsync(agent, config, projectToActivate, loggerFactory);

        // Watch the parent process — when the MCP client (Codex, VS Code, etc.)
        // exits, shut ourselves down so we don't leave Roslyn pinning CPU.
        // The MCP SDK doesn't always propagate client cancel, so this is the
        // safety net that prevents orphan CPU usage after the client closes.
        StartParentProcessWatcher(app, loggerFactory.CreateLogger("ParentWatcher"));

        await app.RunAsync();
    }

    private static void StartParentProcessWatcher(IHost app, ILogger logger)
    {
        var parent = TryGetParentProcess();
        if (parent is null)
        {
            logger.LogDebug("No parent process to watch.");
            return;
        }

        logger.LogDebug("Watching parent process {Pid} ({Name})", parent.Id, parent.ProcessName);
        _ = Task.Run(async () =>
        {
            try
            {
                await parent.WaitForExitAsync();
                logger.LogWarning("Parent process exited — shutting down MCP server.");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Parent watcher error — shutting down anyway");
            }

            try
            {
                await app.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // best-effort
            }
            Environment.Exit(0);
        });
    }

    private static System.Diagnostics.Process? TryGetParentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Cross-platform parent-PID lookup is non-trivial; on non-Windows
            // we rely on signals (SIGHUP/SIGTERM) caught by AppDomain.ProcessExit.
            return null;
        }

        try
        {
            int pid = Environment.ProcessId;
            using var query = new System.Management.ManagementObjectSearcher(
                $"SELECT ParentProcessId FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (System.Management.ManagementObject mo in query.Get())
            {
                int parentPid = Convert.ToInt32(mo["ParentProcessId"]);
                return System.Diagnostics.Process.GetProcessById(parentPid);
            }
        }
        catch
        {
            // Parent may have already exited, or WMI unavailable
        }
        return null;
    }

    /// <summary>
    /// Creates a <see cref="ToolRegistry"/> with all Serena tools.
    /// Extracted as a static factory for clarity and reuse.
    /// </summary>
    public static ToolRegistry BuildToolRegistry(SerenaAgent agent, ILoggerFactory loggerFactory)
    {
        var context = new AgentToolContext(agent, loggerFactory);
        var registry = new ToolRegistry();

        registry.Register(new ReadFileTool(context));
        registry.Register(new CreateTextFileTool(context));
        registry.Register(new ListDirTool(context));
        registry.Register(new FindFileTool(context));
        registry.Register(new SearchForPatternTool(context));
        registry.Register(new FindSymbolTool(context));
        registry.Register(new GetSymbolsOverviewTool(context));
        registry.Register(new FindReferencingSymbolsTool(context));
        registry.Register(new ReadMemoryTool(context));
        registry.Register(new WriteMemoryTool(context));
        registry.Register(new ListMemoriesTool(context));
        registry.Register(new DeleteMemoryTool(context));
        registry.Register(new ExecuteShellCommandTool(context));
        registry.Register(new ActivateProjectTool(context));
        registry.Register(new GetCurrentConfigTool(context));
        registry.Register(new ReplaceSymbolBodyTool(context));
        registry.Register(new InsertBeforeSymbolTool(context));
        registry.Register(new InsertAfterSymbolTool(context));
        registry.Register(new ReplaceContentTool(context));
        registry.Register(new RenameSymbolTool(context));
        registry.Register(new SafeDeleteSymbolTool(context));
        registry.Register(new RenameMemoryTool(context));
        registry.Register(new EditMemoryTool(context));
        registry.Register(new CheckOnboardingPerformedTool(context));
        registry.Register(new OnboardingTool(context));
        registry.Register(new InitialInstructionsTool(context));
        registry.Register(new RemoveProjectTool(context));
        registry.Register(new RestartLanguageServerTool(context));
        registry.Register(new SetActiveSolutionTool(context));
        registry.Register(new ClearActiveSolutionTool(context));
        registry.Register(new KillLanguageServerTool(context));
        registry.Register(new LanguageServerStatusTool(context));
        registry.Register(new WarmLanguageServerTool(context));
        registry.Register(new ListQueryableProjectsTool(context));
        registry.Register(new QueryProjectTool(context));
        registry.Register(new DeleteLinesTool(context));
        registry.Register(new InsertAtLineTool(context));
        registry.Register(new ReplaceLinesTool(context));

        return registry;
    }

    private static string? ParseProjectArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--project", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }

    private static async Task ActivateStartupProjectAsync(
        SerenaAgent agent,
        SerenaConfig config,
        string? projectArg,
        ILoggerFactory loggerFactory)
    {
        string? projectToActivate = projectArg ?? config.DefaultProject;
        if (projectToActivate is null)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger(typeof(Program));
        try
        {
            await agent.ActivateProjectAsync(projectToActivate);
            logger.LogInformation("Auto-activated project: {Project}", projectToActivate);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to auto-activate project {Project}", projectToActivate);
        }
    }
}
