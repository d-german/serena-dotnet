// 0.7.1: activation shows the start of the generated overview instead of a
// pointer to it, and lists only the global memories that belong to the project.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public sealed class ActivateProjectOverviewTests : IDisposable
{
    private readonly string _tempDir;

    public ActivateProjectOverviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_overview_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Activation_InlinesTheStartOfTheGeneratedOverview()
    {
        string memDir = Path.Combine(_tempDir, ".serena", "memories", "generated");
        Directory.CreateDirectory(memDir);
        string overview = "# demo\n\n## Solution and projects\n- Demo.sln: 3 projects (2 source, 1 test)\n\n## Build and test\n- `dotnet build Demo.sln`\n";
        await File.WriteAllTextAsync(Path.Combine(memDir, "project-overview.md"), overview);

        string output = await ActivateAsync();

        output.Should().Contain("Project overview (start of read_memory \"generated/project-overview\"");
        output.Should().Contain("- Demo.sln: 3 projects (2 source, 1 test)");
        output.Should().Contain("- `dotnet build Demo.sln`");
        output.Should().Contain("Available memories (read the project ones before exploring):");
        output.Should().Contain("Onboarding: NOT performed", "the generated overview is facts, not onboarding");
    }

    [Fact]
    public void OverviewExcerpt_StopsAtTheLineLimit_AndSaysHowMuchRemains()
    {
        string content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line {i}"));

        string excerpt = ActivateProjectTool.OverviewExcerpt(content, maxLines: 10, maxChars: 10_000);

        excerpt.Should().Contain("  line 10").And.NotContain("line 11");
        excerpt.Should().EndWith("(90 more lines; read_memory for the rest)");
    }

    [Fact]
    public void OverviewExcerpt_StopsAtTheCharacterLimit_OnALineBoundary()
    {
        string content = string.Join("\n", Enumerable.Range(1, 50).Select(i => new string((char)('a' + i % 26), 40)));

        string excerpt = ActivateProjectTool.OverviewExcerpt(content, maxLines: 100, maxChars: 200);

        excerpt.Split('\n').Should().HaveCountLessThan(8);
        excerpt.Should().Contain("more lines; read_memory for the rest");
    }

    [Fact]
    public void OverviewExcerpt_ReportsAnUnreadableMemory()
    {
        ActivateProjectTool.OverviewExcerpt(null).Should().Contain("could not be read");
    }

    [Fact]
    public void VisibleMemories_HidesGlobalMemoriesFiledUnderOtherProjects()
    {
        string[] all =
        [
            "architecture/overview",
            "generated/project-overview",
            "global/serena-json-escape-pitfall",
            "global/global/shared-note",
            "global/demo/style",
            "global/other-project/suggested_commands",
        ];

        var visible = ActivateProjectTool.VisibleMemories(all, "Demo");

        visible.Should().Equal(
            "architecture/overview",
            "generated/project-overview",
            "global/serena-json-escape-pitfall",
            "global/global/shared-note",
            "global/demo/style");
    }

    private async Task<string> ActivateAsync()
    {
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, new LanguageServerRegistry(), NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        var registry = new ToolRegistry();
        registry.Register(new ActivateProjectTool(context));
        agent.SetToolRegistry(registry);

        var tool = registry.All.First(t => t.Name == "activate_project");
        return await tool.ExecuteAsync(new Dictionary<string, object?> { ["project"] = _tempDir }, CancellationToken.None);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
