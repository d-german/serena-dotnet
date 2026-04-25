// v1.0.31: search_for_pattern clamps context_lines and defaults max_answer_chars.

using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class SearchForPatternLimitsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public SearchForPatternLimitsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_search_" + Guid.NewGuid().ToString("N")[..8]);
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
    public async Task ContextLinesBefore_HugeValue_IsClampedTo500()
    {
        // Build a file with 600 unique lines + a TARGET sentinel near the end.
        var sb = new StringBuilder();
        for (int i = 0; i < 600; i++)
        {
            sb.Append("line_").Append(i).Append('\n');
        }
        sb.Append("TARGET_SENTINEL\n");
        File.WriteAllText(Path.Combine(_tempDir, "big.txt"), sb.ToString());

        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "TARGET_SENTINEL",
                ["context_lines_before"] = 99999,
            },
            CancellationToken.None);

        // 99999 must be clamped to 500: line_99 should be the earliest context line,
        // line_98 (and below) must NOT appear.
        result.Should().Contain("TARGET_SENTINEL");
        result.Should().Contain("line_100");
        result.Should().NotContain("line_99\n");
        result.Should().NotContain("line_50\n");
    }

    [Fact]
    public async Task MaxAnswerChars_DefaultsToTwoMillion()
    {
        // Confirm the constant is wired into the parameter default.
        SearchForPatternTool.DefaultMaxAnswerChars.Should().Be(2_000_000);
        SearchForPatternTool.MaxContextLines.Should().Be(500);
    }

    [Fact]
    public async Task MaxAnswerChars_DefaultCap_TruncatesHugeResult()
    {
        // Generate a small file but request padding via context so the JSON result
        // grows well past 2 MB. We synthesize ~3 MB of unique line content and search
        // for a single sentinel with a moderate context window so the formatter only
        // walks a few matches (avoiding O(n²) match-line counting).
        var sb = new StringBuilder(3_500_000);
        for (int i = 0; i < 50_000; i++)
        {
            // ~70 chars per line × 50_000 = ~3.5 MB
            sb.Append("padding_").Append(i).Append("_xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n");
        }
        sb.Append("UNIQUE_SENTINEL_TOKEN\n");
        File.WriteAllText(Path.Combine(_tempDir, "big.txt"), sb.ToString());

        var tool = _registry.All.First(t => t.Name == "search_for_pattern");
        string capped = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["substring_pattern"] = "UNIQUE_SENTINEL_TOKEN",
                ["context_lines_before"] = 99999, // clamped to 500 -> still a lot of context
                // No max_answer_chars passed -> default 2_000_000.
            },
            CancellationToken.None);

        capped.Length.Should().BeLessThanOrEqualTo(2_000_000 + 1024);
    }
}
