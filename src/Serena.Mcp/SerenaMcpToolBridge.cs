// SerenaMcpToolBridge - Phase A3
// Dynamically bridges Serena ITool instances to MCP SDK McpServerTool instances.

using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Serena.Core.Agent;
using Serena.Core.Tools;

namespace Serena.Mcp;

/// <summary>
/// Bridges Serena's <see cref="ITool"/> instances to MCP <see cref="McpServerTool"/> instances.
/// Each Serena tool becomes an MCP server tool with proper JSON Schema and error handling.
/// </summary>
public static class SerenaMcpToolBridge
{
    /// <summary>
    /// Creates MCP server tools from all tools in a Serena ToolRegistry.
    /// </summary>
    public static IReadOnlyList<McpServerTool> CreateMcpTools(
        ToolRegistry registry, SerenaAgent agent, ILoggerFactory loggerFactory) =>
        registry.All.Select(tool =>
            (McpServerTool)new SerenaMcpServerTool(tool, agent, loggerFactory.CreateLogger<SerenaMcpServerTool>()))
            .ToList();
}

/// <summary>
/// Custom <see cref="McpServerTool"/> that wraps a Serena <see cref="ITool"/>.
/// Provides the tool's JSON Schema from Serena's parameter definitions and
/// routes MCP invocations through to the Serena tool with Result-based error handling.
/// </summary>
internal sealed class SerenaMcpServerTool : McpServerTool
{
    private readonly ITool _tool;
    private readonly SerenaAgent _agent;
    private readonly ILogger _logger;
    private readonly Tool _protocolTool;

    public SerenaMcpServerTool(ITool tool, SerenaAgent agent, ILogger logger)
    {
        _tool = tool;
        _agent = agent;
        _logger = logger;

        using var schemaDoc = ToolBase.GenerateJsonSchema(tool.Parameters);
        _protocolTool = new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = schemaDoc.RootElement.Clone(),
        };
    }

    public override Tool ProtocolTool => _protocolTool;

    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken ct = default)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (request.Params?.Arguments is { } args)
        {
            foreach (var kvp in args)
            {
                arguments[kvp.Key] = UnwrapJsonElement(kvp.Value);
            }
        }

        // Apply per-tool timeout from env var (default 90s, under most MCP client caps).
        TimeSpan timeout = GetToolTimeout();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            string output = await _tool.ExecuteAsync(arguments, linked.Token);
            bool isError = output.StartsWith("Error:", StringComparison.Ordinal);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = output }],
                IsError = isError,
            };
        }
        catch (OperationCanceledException)
        {
            // The caller cancelled OR our timeout fired. Roslyn often ignores LSP
            // cancel during compilation, so force-restart the LS to actually free CPU.
            bool timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            string reason = timedOut
                ? $"timed out after {timeout.TotalSeconds:0}s (SERENA_TOOL_TIMEOUT_SECONDS)"
                : "cancelled by caller";
            _logger.LogWarning("Tool '{Tool}' {Reason} — restarting LSP to release CPU.", _tool.Name, reason);

            // Kill Roslyn process tree IMMEDIATELY (synchronous) so CPU drops before
            // we even return from this method. Don't wait on the agent lock.
            int killed = Serena.Lsp.Process.LanguageServerProcess.KillAllLiveProcesses();
            if (killed > 0)
            {
                _logger.LogWarning("Force-killed {Count} live LSP process tree(s) on cancel.", killed);
            }

            // Then asynchronously rebuild the manager state so the next call works.
            _ = Task.Run(async () =>
            {
                try { await _agent.ForceResetLanguageServerManagerAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "LSP force-reset failed after cancel"); }
            });

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error: tool '{_tool.Name}' {reason}. Language server is being restarted." }],
                IsError = true,
            };
        }
    }

    private static TimeSpan GetToolTimeout()
    {
        string? raw = Environment.GetEnvironmentVariable("SERENA_TOOL_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out int value))
        {
            return TimeSpan.FromSeconds(Math.Clamp(value, 5, 3600));
        }
        return TimeSpan.FromSeconds(90);
    }

    /// <summary>
    /// Unwraps JsonElement values to CLR primitives for tool consumption.
    /// MCP SDK delivers arguments as JsonElements; tools expect strings, ints, bools.
    /// </summary>
    private static object? UnwrapJsonElement(object? value)
    {
        if (value is not JsonElement element)
        {
            return value;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(e => UnwrapJsonElement(e)).ToList<object?>(),
            _ => element.GetRawText(),
        };
    }
}
