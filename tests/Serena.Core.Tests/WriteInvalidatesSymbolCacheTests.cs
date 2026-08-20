using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Core.Tests;

public sealed class WriteInvalidatesSymbolCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SerenaAgent _agent;
    private readonly ToolRegistry _registry;

    public WriteInvalidatesSymbolCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_write_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        _agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(_agent, loggerFactory);

        _registry = new ToolRegistry();
        _registry.Register(new ReplaceContentTool(context));
        _registry.Register(new CreateTextFileTool(context));
        _registry.Register(new DeleteLinesTool(context));
        _registry.Register(new InsertAtLineTool(context));
        _registry.Register(new ReplaceLinesTool(context));
        _registry.Register(new ReplaceSymbolBodyTool(context));
        _registry.Register(new InsertBeforeSymbolTool(context));
        _registry.Register(new InsertAfterSymbolTool(context));
        _registry.Register(new FindSymbolTool(context));

        _agent.SetToolRegistry(_registry);
        _agent.ActivateProjectAsync(_tempDir).Wait();
    }

    [Fact]
    public async Task ReplaceContent_InvalidatesExistingCacheEntry_WithoutStartingLanguageServer()
    {
        string relativePath = "Sample.cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class Before { }\n");
        WriteCacheEntry(absolutePath, "Before");

        var tool = _registry.Get("replace_content")!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["needle"] = "Before",
            ["repl"] = "After",
            ["mode"] = "literal",
        });

        result.Should().Contain("Replaced");
        _agent.GetLanguageServerReadyState(Language.CSharp).State
            .Should().Be(WorkspaceReadyState.NotStarted, "file writes must not cold-start Roslyn just to update cache state");

        var cache = _agent.GetSymbolCache(Language.CSharp);
        cache.Should().NotBeNull();
        cache!.TryGetUnchecked(absolutePath)
            .Should().BeNull("the cached symbols for the old file contents must be removed after the write");
    }

    [Fact]
    public async Task CreateTextFile_Overwrite_InvalidatesExistingCacheEntry_WithoutStartingLanguageServer()
    {
        string relativePath = "Overwrite.cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class OldName { }\n");
        WriteCacheEntry(absolutePath, "OldName");

        var tool = _registry.Get("create_text_file")!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["content"] = "public class NewName { }\n",
        });

        result.Should().Contain("Overwrote existing file");
        _agent.GetLanguageServerReadyState(Language.CSharp).State
            .Should().Be(WorkspaceReadyState.NotStarted);

        var cache = _agent.GetSymbolCache(Language.CSharp);
        cache.Should().NotBeNull();
        cache!.TryGetUnchecked(absolutePath).Should().BeNull();
    }

    [Fact]
    public async Task DeleteLines_InvalidatesExistingCacheEntry_WithoutStartingLanguageServer()
    {
        string relativePath = "DeleteLines.cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class DeleteLinesBefore { }\n");
        WriteCacheEntry(absolutePath, "DeleteLinesBefore");

        var tool = _registry.Get("delete_lines")!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["start_line"] = 1,
            ["end_line"] = 1,
        });

        result.Should().Contain("Deleted");
        AssertCacheEntryRemovedWithoutStartingLanguageServer(absolutePath);
    }

    [Fact]
    public async Task InsertAtLine_InvalidatesExistingCacheEntry_WithoutStartingLanguageServer()
    {
        string relativePath = "InsertLine.cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class InsertLineBefore { }\n");
        WriteCacheEntry(absolutePath, "InsertLineBefore");

        var tool = _registry.Get("insert_at_line")!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["line_number"] = 1,
            ["content"] = "// inserted",
        });

        result.Should().Contain("Inserted");
        AssertCacheEntryRemovedWithoutStartingLanguageServer(absolutePath);
    }

    [Fact]
    public async Task ReplaceLines_InvalidatesExistingCacheEntry_WithoutStartingLanguageServer()
    {
        string relativePath = "ReplaceLines.cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class ReplaceLinesBefore { }\n");
        WriteCacheEntry(absolutePath, "ReplaceLinesBefore");

        var tool = _registry.Get("replace_lines")!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["start_line"] = 1,
            ["end_line"] = 1,
            ["new_content"] = "public class ReplaceLinesAfter { }",
        });

        result.Should().Contain("Replaced");
        AssertCacheEntryRemovedWithoutStartingLanguageServer(absolutePath);
    }

    [Theory]
    [InlineData("replace_symbol_body")]
    [InlineData("insert_before_symbol")]
    [InlineData("insert_after_symbol")]
    public async Task CacheFirstSymbolEdits_InvalidateExistingCacheEntry_WithoutStartingLanguageServer(string toolName)
    {
        string relativePath = toolName + ".cs";
        string absolutePath = Path.Combine(_tempDir, relativePath);
        await File.WriteAllTextAsync(absolutePath, "public class CachedSymbol { }\n");
        WriteCacheEntry(absolutePath, "CachedSymbol");

        var tool = _registry.Get(toolName)!;
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = relativePath,
            ["name_path"] = "CachedSymbol",
            ["body"] = "public class EditedSymbol { }",
        });

        result.Should().Match(s =>
            s.Contains("Replaced", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Inserted", StringComparison.OrdinalIgnoreCase));
        AssertCacheEntryRemovedWithoutStartingLanguageServer(absolutePath);
    }

    [Fact]
    public async Task ReplaceContent_RemovesStaleEntryFromCacheBackedSymbolSearch_WithoutFullReindex()
    {
        string editedRelativePath = "Edited.cs";
        string editedAbsolutePath = Path.Combine(_tempDir, editedRelativePath);
        string untouchedRelativePath = "Untouched.cs";
        string untouchedAbsolutePath = Path.Combine(_tempDir, untouchedRelativePath);
        await File.WriteAllTextAsync(editedAbsolutePath, "public class BeforeWrite { }\n");
        await File.WriteAllTextAsync(untouchedAbsolutePath, "public class StillCached { }\n");
        WriteCacheEntry(editedAbsolutePath, "BeforeWrite");
        WriteCacheEntry(untouchedAbsolutePath, "StillCached");

        var replaceTool = _registry.Get("replace_content")!;
        var replaceResult = await replaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = editedRelativePath,
            ["needle"] = "BeforeWrite",
            ["repl"] = "AfterWrite",
            ["mode"] = "literal",
        });

        replaceResult.Should().Contain("Replaced");
        _agent.GetLanguageServerReadyState(Language.CSharp).State
            .Should().Be(WorkspaceReadyState.NotStarted, "cache invalidation must not require a Roslyn cold start");

        var findTool = _registry.Get("find_symbol")!;
        var staleResult = await findTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "BeforeWrite",
            ["relative_path"] = ".",
        });
        staleResult.Should().Contain("No symbols found matching 'BeforeWrite'",
            "the edited file's old symbol must not remain visible through unchecked cache-backed searches");

        var untouchedResult = await findTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["name_path_pattern"] = "StillCached",
            ["relative_path"] = ".",
        });
        untouchedResult.Should().Contain("StillCached",
            "the test should still be exercising the populated cache path rather than falling back to an empty-cache scan");
        untouchedResult.Should().Contain(untouchedRelativePath);
    }

    private void AssertCacheEntryRemovedWithoutStartingLanguageServer(string absolutePath)
    {
        _agent.GetLanguageServerReadyState(Language.CSharp).State
            .Should().Be(WorkspaceReadyState.NotStarted, "write-time cache invalidation must not require a Roslyn cold start");

        var cache = _agent.GetSymbolCache(Language.CSharp);
        cache.Should().NotBeNull();
        cache!.TryGetUnchecked(absolutePath)
            .Should().BeNull("the edited file's old symbol entry is stale after a write");
    }

    private void WriteCacheEntry(string absolutePath, string symbolName)
    {
        string cacheDir = Path.Combine(_tempDir, ".serena", "cache", "csharp");
        var cache = new SymbolCache<UnifiedSymbolInformation[]>(
            cacheDir,
            "symbols.json",
            1,
            NullLogger.Instance);
        cache.Load();

        var range = new LspRange(new Position(0, 0), new Position(0, 0));
        var symbol = new UnifiedSymbolInformation
        {
            Name = symbolName,
            Kind = SymbolKind.Class,
            BodyRange = range,
            SelectionRange = range,
            Location = new Location("file:///" + absolutePath.Replace('\\', '/'), range),
        };

        cache.Set(absolutePath, CacheFingerprint.ForFile(absolutePath), [symbol]);
        cache.Save();
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
