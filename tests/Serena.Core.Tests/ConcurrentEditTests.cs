// 0.7.1: edits issued in parallel on one file apply in sequence instead of
// overwriting one another. Four replace_symbol_body calls in one second on one
// file lost three of them in a measured run.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Editor;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public sealed class ConcurrentEditTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ToolRegistry _registry;

    public ConcurrentEditTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_concurrent_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var agent = new SerenaAgent(config, new LanguageServerRegistry(), NullLoggerFactory.Instance);
        var context = new AgentToolContext(agent, NullLoggerFactory.Instance);
        _registry = new ToolRegistry();
        _registry.Register(new ReplaceContentTool(context));
        _registry.Register(new ApplyEditsTool(context));
        _registry.Register(new ReplaceLinesTool(context));
        agent.SetToolRegistry(_registry);
        agent.ActivateProjectAsync(_tempDir).Wait();
    }

    [Fact]
    public async Task ParallelReplaceContentCallsOnOneFile_AllLand()
    {
        const int edits = 12;
        string path = Path.Combine(_tempDir, "Tokens.cs");
        await File.WriteAllTextAsync(path, string.Join("\n", Enumerable.Range(0, edits).Select(i => $"// token_{(char)('a' + i)};")) + "\n");
        var tool = _registry.Get("replace_content")!;

        var results = await Task.WhenAll(Enumerable.Range(0, edits).Select(i => tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = "Tokens.cs",
            ["needle"] = $"token_{(char)('a' + i)};",
            ["repl"] = $"done_{(char)('a' + i)};",
            ["mode"] = "literal",
        })));

        results.Should().OnlyContain(r => r.StartsWith("Replaced 1 occurrence"));
        string final = await File.ReadAllTextAsync(path);
        for (int i = 0; i < edits; i++)
        {
            final.Should().Contain($"done_{(char)('a' + i)};").And.NotContain($"token_{(char)('a' + i)};");
        }
    }

    [Fact]
    public async Task ParallelMixedToolsOnOneFile_AllLand()
    {
        string path = Path.Combine(_tempDir, "Mixed.cs");
        await File.WriteAllTextAsync(path, "line1\nline2\nline3\nline4\n");
        var replaceContent = _registry.Get("replace_content")!;
        var applyEdits = _registry.Get("apply_edits")!;
        var replaceLines = _registry.Get("replace_lines")!;

        await Task.WhenAll(
            replaceContent.ExecuteAsync(new Dictionary<string, object?>
            {
                ["relative_path"] = "Mixed.cs", ["needle"] = "line1", ["repl"] = "first", ["mode"] = "literal",
            }),
            applyEdits.ExecuteAsync(new Dictionary<string, object?>
            {
                ["edits"] = new List<Dictionary<string, object?>>
                {
                    new() { ["relative_path"] = "Mixed.cs", ["needle"] = "line2", ["repl"] = "second" },
                },
            }),
            replaceLines.ExecuteAsync(new Dictionary<string, object?>
            {
                ["relative_path"] = "Mixed.cs", ["start_line"] = 4, ["end_line"] = 4, ["new_content"] = "fourth",
            }));

        string final = await File.ReadAllTextAsync(path);
        final.Should().Be("first\nsecond\nline3\nfourth\n");
    }

    [Fact]
    public async Task TheLeaseIsHeldAcrossTheWholeEdit()
    {
        string path = Path.Combine(_tempDir, "Lease.cs");
        await File.WriteAllTextAsync(path, "a\n");

        await using var lease = await FileWriteGate.LockAsync(path);
        var second = FileWriteGate.LockAsync(path);
        await Task.Delay(100);
        second.IsCompleted.Should().BeFalse("the second caller waits for the first lease");

        await lease.WriteAllTextAsync("b\n", encoding: null);
        await lease.DisposeAsync();
        await using var acquired = await second;
        (await File.ReadAllTextAsync(path)).Should().Be("b\n");
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
