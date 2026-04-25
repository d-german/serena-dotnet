// v1.0.31: read_file rejects negative start_line/end_line; preserves end clamp.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class ReadFileLineValidationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public ReadFileLineValidationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_readfile_v_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(Path.Combine(_tempDir, "small.txt"), "alpha\nbravo\ncharlie\n");

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Task<string> CallAsync(IDictionary<string, object?> args)
    {
        var tool = _registry.All.First(t => t.Name == "read_file");
        return tool.ExecuteAsync(new Dictionary<string, object?>(args), CancellationToken.None);
    }

    [Fact]
    public async Task NegativeStartLine_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "small.txt",
            ["start_line"] = -10,
        });
        r.Should().Be("start_line must be >= 0");
    }

    [Fact]
    public async Task NegativeEndLine_IsRejected()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "small.txt",
            ["end_line"] = -1,
        });
        r.Should().Be("end_line must be >= 0");
    }

    [Fact]
    public async Task EndLineWayPastFileLength_IsClamped_ReturnsFullContent()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "small.txt",
            ["end_line"] = 99999,
        });
        r.Should().Contain("alpha");
        r.Should().Contain("bravo");
        r.Should().Contain("charlie");
    }

    [Fact]
    public async Task NoLineArgs_ReturnsFullContent()
    {
        string r = await CallAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "small.txt",
        });
        r.Should().Contain("alpha");
        r.Should().Contain("charlie");
    }
}
