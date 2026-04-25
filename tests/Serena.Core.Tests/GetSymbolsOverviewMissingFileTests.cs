// v1.0.31: get_symbols_overview returns File not found without warming LSP.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class GetSymbolsOverviewMissingFileTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public GetSymbolsOverviewMissingFileTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_overview_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new GetSymbolsOverviewTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task NonExistentPath_ReturnsFileNotFound_WithoutWarming()
    {
        var tool = _registry.All.First(t => t.Name == "get_symbols_overview");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = "does_not_exist.cs",
            },
            CancellationToken.None);

        result.Should().Be("File not found: does_not_exist.cs");
        result.Should().NotContain("language_server_warming");
    }
}
