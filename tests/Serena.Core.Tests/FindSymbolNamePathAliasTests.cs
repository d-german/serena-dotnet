// v1.0.31: find_symbol accepts name_path as alias for name_path_pattern.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class FindSymbolNamePathAliasTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public FindSymbolNamePathAliasTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_findsym_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(Path.Combine(_tempDir, "Sample.cs"),
            "namespace N { public class Sample { public void DoThing() {} } }");

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new FindSymbolTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task NamePathAlias_BehavesIdenticallyToCanonical()
    {
        var tool = _registry.All.First(t => t.Name == "find_symbol");

        string canonical = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["name_path_pattern"] = "Sample",
                ["relative_path"] = "Sample.cs",
            },
            CancellationToken.None);

        string aliased = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["name_path"] = "Sample",
                ["relative_path"] = "Sample.cs",
            },
            CancellationToken.None);

        aliased.Should().Be(canonical);
    }
}
