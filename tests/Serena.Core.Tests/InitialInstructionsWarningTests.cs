// Tests for InitialInstructionsTool's multi-solution warning prefix.
// Verifies the warning fires only when (a) >=2 .sln/.slnx files exist under
// the project root AND (b) no C# scope is currently set.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Project;

namespace Serena.Core.Tests;

[Collection("CSharpScopeEnvSerial")]
public class InitialInstructionsWarningTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalEnv;

    public InitialInstructionsWarningTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena-iiwarn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalEnv = Environment.GetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar) ?? "";
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar,
            string.IsNullOrEmpty(_originalEnv) ? null : _originalEnv);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task NoActiveProject_NoWarning()
    {
        var tool = new InitialInstructionsTool(NullToolContext.Instance);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);
        Assert.DoesNotContain("Multiple C# solutions detected", result);
    }

    [Fact]
    public async Task SingleSolution_NoWarning()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Only.sln"), "");
        var ctx = BuildContextWithProject();
        var tool = new InitialInstructionsTool(ctx);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);
        Assert.DoesNotContain("Multiple C# solutions detected", result);
    }

    [Fact]
    public async Task MultipleSolutions_NoScope_WarningPrepended()
    {
        File.WriteAllText(Path.Combine(_tempDir, "A.sln"), "");
        File.WriteAllText(Path.Combine(_tempDir, "B.slnx"), "<Solution></Solution>");

        var ctx = BuildContextWithProject();
        var tool = new InitialInstructionsTool(ctx);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("Multiple C# solutions detected", result);
        Assert.Contains("(2 found)", result);
        Assert.Contains("set_active_solution", result);
        Assert.Contains("search_for_pattern", result);
        // Warning must NOT enumerate solutions blindly — that defeats the discovery workflow.
        Assert.DoesNotContain("A.sln", result);
        Assert.DoesNotContain("B.slnx", result);
    }

    [Fact]
    public async Task MultipleSolutions_WithScope_NoWarning()
    {
        var slnA = Path.Combine(_tempDir, "A.sln");
        File.WriteAllText(slnA, "");
        File.WriteAllText(Path.Combine(_tempDir, "B.sln"), "");

        var ctx = BuildContextWithProject();
        ctx.ActiveProject!.SetCSharpScope(SolutionScope.FromSolutions(slnA));

        var tool = new InitialInstructionsTool(ctx);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);
        Assert.DoesNotContain("Multiple C# solutions detected", result);
    }

    private FakeToolContext BuildContextWithProject()
    {
        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        return new FakeToolContext(agent, project, loggerFactory);
    }

    private sealed class FakeToolContext : IToolContext
    {
        public FakeToolContext(SerenaAgent agent, SerenaProject project, ILoggerFactory lf)
        {
            Agent = agent;
            ActiveProject = project;
            LoggerFactory = lf;
        }
        public SerenaAgent Agent { get; }
        public SerenaProject? ActiveProject { get; }
        public string? ProjectRoot => ActiveProject?.Root;
        public ILoggerFactory LoggerFactory { get; }
    }
}
