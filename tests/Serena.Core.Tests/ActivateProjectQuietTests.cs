// v1.0.31: ActivateProjectTool suppresses "Onboarding: performed" repetition.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class ActivateProjectQuietTests : IDisposable
{
    private readonly string _tempDir;

    public ActivateProjectQuietTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_activate_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private async Task<string> ActivateAsync()
    {
        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        var registry = new ToolRegistry();
        registry.Register(new ActivateProjectTool(context));
        agent.SetToolRegistry(registry);

        var tool = registry.All.First(t => t.Name == "activate_project");
        return await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["project"] = _tempDir },
            CancellationToken.None);
    }

    [Fact]
    public async Task NotOnboarded_StillEmitsNudge()
    {
        // No .serena/memories yet → projectMemories empty → nudge should be emitted.
        string output = await ActivateAsync();

        output.Should().Contain("Onboarding: NOT performed");
        output.Should().Contain("Consider calling the 'onboarding' tool");
    }

    [Fact]
    public async Task Onboarded_NoOtherMemories_SuppressesOnboardingPerformedAndAvailableMemories()
    {
        // Pre-seed a project memory to mark onboarding as performed.
        string memDir = Path.Combine(_tempDir, ".serena", "memories");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "project_overview.md"), "# Overview");

        string output = await ActivateAsync();

        output.Should().NotContain("Onboarding: performed");
        output.Should().NotContain("Onboarding: NOT performed");
        // "Available memories:" header MUST still appear because there is one.
        output.Should().Contain("Available memories:");
        output.Should().Contain("project_overview");
    }
}
