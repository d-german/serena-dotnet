using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Mcp.Tests;

public class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assert.NotNull(typeof(Serena.Mcp.Program));
    }

    [Fact]
    public void CreateMcpTools_BridgesAllRegisteredTools()
    {
        var (agent, registry) = CreateTestRegistryWithTools(
            ctx => new ReadFileTool(ctx),
            ctx => new ListDirTool(ctx),
            ctx => new FindFileTool(ctx));

        var mcpTools = SerenaMcpToolBridge.CreateMcpTools(registry);

        Assert.Equal(3, mcpTools.Count);
        Assert.Contains(mcpTools, t => t.ProtocolTool.Name == "read_file");
        Assert.Contains(mcpTools, t => t.ProtocolTool.Name == "list_dir");
        Assert.Contains(mcpTools, t => t.ProtocolTool.Name == "find_file");
    }

    [Fact]
    public void McpTool_HasCorrectSchemaWithProperties()
    {
        var (_, registry) = CreateTestRegistryWithTools(ctx => new ReadFileTool(ctx));

        var mcpTools = SerenaMcpToolBridge.CreateMcpTools(registry);
        var tool = mcpTools[0];

        Assert.Equal("read_file", tool.ProtocolTool.Name);
        Assert.False(string.IsNullOrEmpty(tool.ProtocolTool.Description));

        // Schema should have "type": "object" and "properties"
        var schema = tool.ProtocolTool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.TryGetProperty("properties", out var props));
        Assert.True(props.EnumerateObject().Any());
    }

    private static (SerenaAgent Agent, ToolRegistry Registry) CreateTestRegistryWithTools(
        params Func<IToolContext, ToolBase>[] toolFactories)
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        var registry = new ToolRegistry();
        foreach (var factory in toolFactories)
        {
            registry.Register(factory(context));
        }
        agent.SetToolRegistry(registry);

        return (agent, registry);
    }
}
