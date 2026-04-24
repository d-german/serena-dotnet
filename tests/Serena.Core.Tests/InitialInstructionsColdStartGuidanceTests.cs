// v1.0.26: Cold-start workflow guidance is appended to the multi-solution
// warning section of InitialInstructionsTool. This test verifies the
// substring stays in place so the next refactor doesn't quietly drop it.

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
public class InitialInstructionsColdStartGuidanceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalEnv;

    public InitialInstructionsColdStartGuidanceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena-iicold-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalEnv = Environment.GetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar) ?? "";
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar,
            string.IsNullOrEmpty(_originalEnv) ? null : _originalEnv);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MultipleSolutions_WarningContainsColdStartGuidance()
    {
        File.WriteAllText(Path.Combine(_tempDir, "A.sln"), "");
        File.WriteAllText(Path.Combine(_tempDir, "B.slnx"), "<Solution></Solution>");

        var ctx = BuildContextWithProject();
        var tool = new InitialInstructionsTool(ctx);

        string result = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        Assert.Contains("warming", result);
        Assert.Contains("get_language_server_status", result);
        Assert.Contains("search_for_pattern", result);
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
