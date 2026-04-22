// Security-focused tests for code paths that handle untrusted input.

using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Hooks;
using Serena.Core.Project;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class SecurityTests
{
    // ── Path Traversal Prevention (ToolBase.ResolvePath) ──────────────────

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("..\\..\\Windows\\System32\\config\\SAM")]
    [InlineData("subdir/../../..")]
    [InlineData("a/../../../outside")]
    public async Task ResolvePath_BlocksPathTraversal(string maliciousPath)
    {
        // ResolvePath is protected, so exercise it through ReadFileTool which calls it
        var ctx = CreateContextWithProject();
        var tool = new ReadFileTool(ctx);
        var args = new Dictionary<string, object?> { ["relative_path"] = maliciousPath };

        var result = await tool.ExecuteAsync(args);

        result.Should().StartWith("Error:");
        result.Should().Contain("outside the project root");
    }

    [Fact]
    public async Task ResolvePath_AllowsValidSubpath()
    {
        var ctx = CreateContextWithProject();
        var tool = new ReadFileTool(ctx);
        // This path is within the project root but file won't exist — that's fine,
        // we're testing that it doesn't throw UnauthorizedAccessException.
        var args = new Dictionary<string, object?> { ["relative_path"] = "subdir/file.txt" };

        var result = await tool.ExecuteAsync(args);

        // Should get "file not found", not "outside project root"
        result.Should().Contain("not found");
    }

    // ── Memory Path Validation ───────────────────────────────────────────

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("topic/../../../escape")]
    [InlineData("..")]
    public void MemoriesManager_BlocksPathTraversal(string maliciousName)
    {
        using var tempDir = new TempDirectory();
        var mm = new MemoriesManager(tempDir.Path);

        var act = () => mm.GetMemoryFilePath(maliciousName);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*'..'*");
    }

    [Fact]
    public void MemoriesManager_AllowsValidMemoryName()
    {
        using var tempDir = new TempDirectory();
        var mm = new MemoriesManager(tempDir.Path);

        var path = mm.GetMemoryFilePath("notes");

        path.Should().EndWith("notes.md");
    }

    // ── Hook System: Deny Blocks Execution ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DenyHook_BlocksExecution()
    {
        var ctx = CreateFreshAgentContext();
        ctx.Agent.RegisterHook(new AlwaysDenyHook("Blocked by policy"));

        var tool = new EchoTool(ctx);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        result.Should().StartWith("Error:");
        result.Should().Contain("Blocked by policy");
    }

    [Fact]
    public async Task ExecuteAsync_AllowWithMessageHook_PrependsMessage()
    {
        var ctx = CreateFreshAgentContext();
        ctx.Agent.RegisterHook(new AllowWithMessageHook("WARNING: use caution"));

        var tool = new EchoTool(ctx);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        result.Should().StartWith("WARNING: use caution");
        result.Should().Contain("echo-ok");
    }

    // ── Regex Timeout Protection ─────────────────────────────────────────

    [Fact]
    public void TextReplacementHelper_CreateSearchRegex_HasTimeout()
    {
        var regex = TextReplacementHelper.CreateSearchRegex("test");

        regex.MatchTimeout.Should().BeGreaterThan(TimeSpan.Zero);
        regex.MatchTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void TextReplacementHelper_CreateSearchRegex_MatchesPattern()
    {
        // CreateSearchRegex treats input as regex pattern (not escaped literal)
        var regex = TextReplacementHelper.CreateSearchRegex("hello\\.world");

        regex.IsMatch("hello.world").Should().BeTrue();
        regex.IsMatch("helloXworld").Should().BeFalse();
    }

    // ── SerenaProject.ValidatePath ───────────────────────────────────────

    [Theory]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("/etc/passwd")]
    public void SerenaProject_ValidatePath_RejectsPathsOutsideRoot(string externalPath)
    {
        using var tempDir = new TempDirectory();
        var project = new SerenaProject(tempDir.Path, NullLogger<SerenaProject>.Instance);

        var act = () => project.ValidatePath(externalPath);

        act.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*outside the project root*");
    }

    [Fact]
    public void SerenaProject_ValidatePath_AllowsPathInsideRoot()
    {
        using var tempDir = new TempDirectory();
        var project = new SerenaProject(tempDir.Path, NullLogger<SerenaProject>.Instance);

        string insidePath = Path.Combine(tempDir.Path, "src", "file.cs");
        var act = () => project.ValidatePath(insidePath);

        act.Should().NotThrow();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static FreshAgentContext CreateFreshAgentContext()
    {
        return new FreshAgentContext();
    }

    private static ProjectToolContext CreateContextWithProject()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "serena-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        var project = new SerenaProject(tempRoot, NullLogger<SerenaProject>.Instance);
        return new ProjectToolContext(project, tempRoot);
    }

    private sealed class AlwaysDenyHook(string message) : IPreToolUseHook
    {
        public Task<(HookResult Result, string? Message)> OnBeforeToolUseAsync(
            string toolName, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
            => Task.FromResult((HookResult.Deny, (string?)message));
    }

    private sealed class AllowWithMessageHook(string message) : IPreToolUseHook
    {
        public Task<(HookResult Result, string? Message)> OnBeforeToolUseAsync(
            string toolName, IReadOnlyDictionary<string, object?> parameters, CancellationToken ct)
            => Task.FromResult((HookResult.AllowWithMessage, (string?)message));
    }

    private sealed class EchoTool : ToolBase
    {
        public EchoTool(IToolContext context) : base(context) { }
        public override string Description => "Returns a fixed string";
        protected override IReadOnlyList<ToolParameter> ExtractParameters() => [];
        protected override Task<string> ApplyAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
            => Task.FromResult("echo-ok");
    }

    private sealed class FreshAgentContext : IToolContext
    {
        private readonly SerenaAgent _agent = new(
            new SerenaConfig(NullLogger<SerenaConfig>.Instance),
            new Serena.Lsp.LanguageServers.LanguageServerRegistry(),
            NullLoggerFactory.Instance);

        public SerenaAgent Agent => _agent;
        public SerenaProject? ActiveProject => null;
        public string? ProjectRoot => null;
        public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;
    }

    private sealed class ProjectToolContext : IToolContext
    {
        private readonly Lazy<SerenaAgent> _agent = new(() =>
            new SerenaAgent(
                new SerenaConfig(NullLogger<SerenaConfig>.Instance),
                new Serena.Lsp.LanguageServers.LanguageServerRegistry(),
                NullLoggerFactory.Instance));

        public ProjectToolContext(SerenaProject project, string projectRoot)
        {
            ActiveProject = project;
            ProjectRoot = projectRoot;
        }

        public SerenaAgent Agent => _agent.Value;
        public SerenaProject? ActiveProject { get; }
        public string? ProjectRoot { get; }
        public ILoggerFactory LoggerFactory { get; } = NullLoggerFactory.Instance;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "serena-test-" + Guid.NewGuid().ToString("N")[..8]);

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* cleanup is best-effort */ }
        }
    }
}
