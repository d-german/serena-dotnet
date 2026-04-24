// v1.0.28: list_dir defaults skip_ignored_files=true when recursive=true.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class ListDirIgnoreDefaultsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public ListDirIgnoreDefaultsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_listdir_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // .gitignore that excludes bin/ and obj/
        File.WriteAllText(Path.Combine(_tempDir, ".gitignore"), "bin/\nobj/\n");

        Directory.CreateDirectory(Path.Combine(_tempDir, "src"));
        File.WriteAllText(Path.Combine(_tempDir, "src", "A.cs"), "public class A {}");

        Directory.CreateDirectory(Path.Combine(_tempDir, "bin", "Debug"));
        File.WriteAllText(Path.Combine(_tempDir, "bin", "Debug", "A.dll"), "binary");

        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        File.WriteAllText(Path.Combine(_tempDir, "obj", "x.json"), "{}");

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new ListDirTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RecursiveTrue_DefaultsToSkipIgnored_ExcludesBinAndObj()
    {
        var tool = _registry.All.First(t => t.Name == "list_dir");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = ".",
                ["recursive"] = true,
                // skip_ignored_files NOT specified → should default to true
            },
            CancellationToken.None);

        result.Should().Contain("A.cs");
        result.Should().NotContain("A.dll");
        result.Should().NotContain("x.json");
    }

    [Fact]
    public async Task RecursiveTrue_ExplicitSkipIgnoredFalse_IncludesBinAndObj()
    {
        var tool = _registry.All.First(t => t.Name == "list_dir");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = ".",
                ["recursive"] = true,
                ["skip_ignored_files"] = false,
            },
            CancellationToken.None);

        result.Should().Contain("A.cs");
        result.Should().Contain("A.dll");
        result.Should().Contain("x.json");
    }

    [Fact]
    public async Task RecursiveFalse_DefaultsToIncludeAll_TopLevelUnchanged()
    {
        var tool = _registry.All.First(t => t.Name == "list_dir");
        string result = await tool.ExecuteAsync(
            new Dictionary<string, object?>
            {
                ["relative_path"] = ".",
                ["recursive"] = false,
            },
            CancellationToken.None);

        // Top-level lists src, bin, obj as directories regardless of gitignore
        result.Should().Contain("src");
        result.Should().Contain("bin");
        result.Should().Contain("obj");
    }
}
