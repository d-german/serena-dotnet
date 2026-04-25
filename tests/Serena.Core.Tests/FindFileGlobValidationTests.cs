// v1.0.31: find_file surfaces invalid glob errors.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class FindFileGlobValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public FindFileGlobValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_findfile_v_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(Path.Combine(_tempDir, "alpha.cs"), "");
        File.WriteAllText(Path.Combine(_tempDir, "bravo.cs"), "");

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new FindFileTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Task<string> CallAsync(string mask) =>
        _registry.All.First(t => t.Name == "find_file").ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["file_mask"] = mask,
                ["relative_path"] = ".",
            },
            CancellationToken.None);

    [Fact]
    public async Task UnbalancedBracket_ReturnsInvalidGlob()
    {
        string r = await CallAsync("[");
        r.Should().StartWith("invalid glob:");
    }

    [Fact]
    public async Task ValidWildcard_StillWorks()
    {
        string r = await CallAsync("*.cs");
        r.Should().Contain("alpha.cs");
        r.Should().Contain("bravo.cs");
    }
}
