// v1.0.31: unified path-type-mismatch error messages across read_file, list_dir, find_file.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public class PathTypeMismatchTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public PathTypeMismatchTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_pathtype_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "subdir"));
        File.WriteAllText(Path.Combine(_tempDir, "afile.txt"), "data");

        var lsRegistry = new LanguageServerRegistry();
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, lsRegistry, NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new ReadFileTool(context));
        _registry.Register(new ListDirTool(context));
        _registry.Register(new FindFileTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private Task<string> CallAsync(string toolName, IDictionary<string, object?> args) =>
        _registry.All.First(t => t.Name == toolName).ExecuteAsync(
            new Dictionary<string, object?>(args), CancellationToken.None);

    [Fact]
    public async Task ReadFile_OnDirectory_ReturnsFileExpectedError()
    {
        string r = await CallAsync("read_file", new Dictionary<string, object?>
        {
            ["relative_path"] = "subdir",
        });
        r.Should().Be("Path 'subdir' is a directory, expected a file.");
    }

    [Fact]
    public async Task ReadFile_OnMissingPath_ReturnsPathNotFound()
    {
        string r = await CallAsync("read_file", new Dictionary<string, object?>
        {
            ["relative_path"] = "nope.txt",
        });
        r.Should().Be("Path not found: nope.txt");
    }

    [Fact]
    public async Task ListDir_OnFile_ReturnsDirectoryExpectedError()
    {
        string r = await CallAsync("list_dir", new Dictionary<string, object?>
        {
            ["relative_path"] = "afile.txt",
            ["recursive"] = false,
        });
        r.Should().Be("Path 'afile.txt' is a file, expected a directory.");
    }

    [Fact]
    public async Task ListDir_OnMissingPath_ReturnsPathNotFound()
    {
        string r = await CallAsync("list_dir", new Dictionary<string, object?>
        {
            ["relative_path"] = "nope_dir",
            ["recursive"] = false,
        });
        r.Should().Be("Path not found: nope_dir");
    }

    [Fact]
    public async Task FindFile_OnFile_ReturnsDirectoryExpectedError()
    {
        string r = await CallAsync("find_file", new Dictionary<string, object?>
        {
            ["file_mask"] = "*.txt",
            ["relative_path"] = "afile.txt",
        });
        r.Should().Be("Path 'afile.txt' is a file, expected a directory.");
    }

    [Fact]
    public async Task FindFile_OnMissingPath_ReturnsPathNotFound()
    {
        string r = await CallAsync("find_file", new Dictionary<string, object?>
        {
            ["file_mask"] = "*.txt",
            ["relative_path"] = "nope_dir",
        });
        r.Should().Be("Path not found: nope_dir");
    }
}
