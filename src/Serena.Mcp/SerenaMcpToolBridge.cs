// SerenaMcpToolBridge - Phase A3
// Dynamically bridges Serena ITool instances to MCP SDK McpServerTool instances.

using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    public static IReadOnlyList<McpServerTool> CreateMcpTools(ToolRegistry registry) =>
        registry.All.Select(tool => (McpServerTool)new SerenaMcpServerTool(tool)).ToList();
}

/// <summary>
/// Custom <see cref="McpServerTool"/> that wraps a Serena <see cref="ITool"/>.
/// Provides the tool's JSON Schema from Serena's parameter definitions and
/// routes MCP invocations through to the Serena tool with Result-based error handling.
/// </summary>
internal sealed class SerenaMcpServerTool : McpServerTool
{
    private readonly ITool _tool;
    private readonly Tool _protocolTool;

    public SerenaMcpServerTool(ITool tool)
    {
        _tool = tool;

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

        // Use ITool.ExecuteAsync to go through the hook pipeline (ExecuteSafeAsync bypasses hooks).
        string output = await _tool.ExecuteAsync(arguments, ct);
        bool isError = output.StartsWith("Error:", StringComparison.Ordinal);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = output }],
            IsError = isError,
        };
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
