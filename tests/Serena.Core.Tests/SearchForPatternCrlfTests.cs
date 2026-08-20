// v0.1.2 regression: search_for_pattern must not emit \r\r\n line terminators
// when the source file is CRLF. The renderer strips a trailing CR before
// StringBuilder.AppendLine appends Environment.NewLine.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class SearchForPatternCrlfTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public SearchForPatternCrlfTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_crlf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new SearchForPatternTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task CrlfFile_OutputDoesNotContainDoubleCr()
    {
        // Author a CRLF-terminated file with a unique sentinel.
        var content = "alpha\r\nbeta\r\nTARGET_SENTINEL\r\ngamma\r\n";
        File.WriteAllText(Path.Combine(_tempDir, "crlf.txt"), content);

        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "TARGET_SENTINEL",
                ["context_lines_before"] = 1,
                ["context_lines_after"] = 1,
            },
            CancellationToken.None);

        result.Should().Contain("TARGET_SENTINEL");
        result.Should().NotContain("\r\r\n", because: "the renderer must strip the trailing CR before AppendLine appends \\r\\n on Windows");
        // And of course no triple-CR pathological case either.
        result.Should().NotContain("\r\r\r");
    }

    [Fact]
    public async Task LfOnlyFile_OutputUnaffected()
    {
        // LF-only file should still produce normal output (regression guard for the trim helper).
        var content = "alpha\nbeta\nTARGET_SENTINEL\ngamma\n";
        File.WriteAllText(Path.Combine(_tempDir, "lf.txt"), content);

        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "TARGET_SENTINEL",
            },
            CancellationToken.None);

        result.Should().Contain("TARGET_SENTINEL");
        result.Should().NotContain("\r\r\n");
    }

    [Fact]
    public async Task NearbyMatches_MergeOverlappingContextWithoutRepeatingLines()
    {
        var content = "zero\none TARGET\ntwo\nthree TARGET\nfour\n";
        File.WriteAllText(Path.Combine(_tempDir, "overlap.txt"), content);

        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "TARGET",
                ["relative_path"] = "overlap.txt",
                ["context_lines_before"] = 2,
                ["context_lines_after"] = 2,
            },
            CancellationToken.None);

        result.Split("2:one TARGET", StringSplitOptions.None).Should().HaveCount(2,
            because: "the first matching line should be emitted exactly once");
        result.Split("3:two", StringSplitOptions.None).Should().HaveCount(2,
            because: "overlapping context lines should be emitted exactly once");
        result.Split("4:three TARGET", StringSplitOptions.None).Should().HaveCount(2,
            because: "the second matching line should be emitted exactly once");
    }
}
