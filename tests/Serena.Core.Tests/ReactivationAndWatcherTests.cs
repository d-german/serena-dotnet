// 0.7.1: activating the active project again is a reconcile, not a restart, and
// files written by something other than serena's tools reach the index as they land.

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

public sealed class ReactivationAndWatcherTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly SerenaAgent _agent;

    public ReactivationAndWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_reactivate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        _agent = new SerenaAgent(config, new LanguageServerRegistry(), NullLoggerFactory.Instance);
        var context = new AgentToolContext(_agent, NullLoggerFactory.Instance);
        var registry = new ToolRegistry();
        registry.Register(new FindSymbolTool(context));
        _agent.SetToolRegistry(registry);
    }

    [Fact]
    public async Task ActivatingTheActiveProjectAgain_KeepsTheLanguageServerManager()
    {
        await _agent.ActivateProjectAsync(_tempDir);
        var manager = _agent.LanguageServerManager;
        manager.Should().NotBeNull();

        await _agent.ActivateProjectAsync(_tempDir + Path.DirectorySeparatorChar);

        _agent.LanguageServerManager.Should().BeSameAs(manager, "re-activation must not dispose the caches, pending outlines or a running server");
        _agent.ActiveProject!.Root.Should().Be(_agent.ActiveProject.Root);
    }

    [Fact]
    public async Task ActivatingAMissingPath_LeavesTheActiveProjectUntouched()
    {
        await _agent.ActivateProjectAsync(_tempDir);
        var manager = _agent.LanguageServerManager;

        var result = await _agent.TryActivateProjectAsync(Path.Combine(_tempDir, "does-not-exist"));

        result.IsFailure.Should().BeTrue();
        _agent.ActiveProject.Should().NotBeNull();
        _agent.LanguageServerManager.Should().BeSameAs(manager);
    }

    [Fact]
    public async Task ReactivationReconcilesTheIndex_WithoutAWatcherRestart()
    {
        string sample = Path.Combine(_tempDir, "Sample.cs");
        await File.WriteAllTextAsync(sample, "public class Sample { }\n");
        WriteCacheEntry(sample, "Sample");
        await _agent.ActivateProjectAsync(_tempDir);
        var watcher = _agent.FileWatcher;
        watcher.Should().NotBeNull("a project with an index is watched");

        await File.WriteAllTextAsync(sample, "public class Renamed { }\n");
        await _agent.ActivateProjectAsync(_tempDir);

        _agent.FileWatcher.Should().BeSameAs(watcher);
        var names = await WaitForSymbolsAsync(sample, s => s.Contains("Renamed"));
        names.Should().Contain("Renamed").And.NotContain("Sample");
    }

    [Fact]
    public async Task AFileCreatedByAnotherTool_EntersTheIndex_AndLeavesItWhenDeleted()
    {
        string sample = Path.Combine(_tempDir, "Sample.cs");
        await File.WriteAllTextAsync(sample, "public class Sample { }\n");
        WriteCacheEntry(sample, "Sample");
        await _agent.ActivateProjectAsync(_tempDir);
        _agent.FileWatcher.Should().NotBeNull();

        string created = Path.Combine(_tempDir, "Created.cs");
        await File.WriteAllTextAsync(created, "namespace N;\npublic class Created { public void Run() { } }\n");

        var names = await WaitForSymbolsAsync(created, s => s.Contains("Created"));
        names.Should().Contain("Created").And.Contain("Run");
        _agent.GetLanguageServerReadyState(Language.CSharp).State
            .Should().Be(WorkspaceReadyState.NotStarted, "the parser outlines the file; no server starts");

        File.Delete(created);
        await WaitUntilAsync(() => Cache().TryGetUnchecked(created) is null);
        Cache().TryGetUnchecked(created).Should().BeNull();
    }

    [Fact]
    public async Task AFolderWrittenByAnotherTool_IsReconciled()
    {
        string sample = Path.Combine(_tempDir, "Sample.cs");
        await File.WriteAllTextAsync(sample, "public class Sample { }\n");
        WriteCacheEntry(sample, "Sample");
        await _agent.ActivateProjectAsync(_tempDir);

        string staging = Path.Combine(Path.GetTempPath(), "serena_staging_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(staging, "Moved.cs"), "public class Moved { }\n");
        string target = Path.Combine(_tempDir, "Feature");
        Directory.Move(staging, target);

        string moved = Path.Combine(target, "Moved.cs");
        var names = await WaitForSymbolsAsync(moved, s => s.Contains("Moved"));
        names.Should().Contain("Moved");
    }

    private SymbolCache<UnifiedSymbolInformation[]> Cache() =>
        _agent.GetSymbolCache(Language.CSharp) ?? throw new InvalidOperationException("no cache");

    private async Task<List<string>> WaitForSymbolsAsync(string absolutePath, Func<List<string>, bool> ready)
    {
        List<string> names = [];
        await WaitUntilAsync(() =>
        {
            var outline = Cache().TryGetUnchecked(absolutePath);
            names = outline is null ? [] : Flatten(outline).Select(s => s.Name).ToList();
            return ready(names);
        });
        return names;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static IEnumerable<UnifiedSymbolInformation> Flatten(IEnumerable<UnifiedSymbolInformation> symbols) =>
        symbols.SelectMany(s => new[] { s }.Concat(Flatten(s.Children)));

    private void WriteCacheEntry(string absolutePath, string symbolName)
    {
        string cacheDir = Path.Combine(_tempDir, ".serena", "cache", "csharp");
        var cache = new SymbolCache<UnifiedSymbolInformation[]>(
            cacheDir, "symbols.json", Serena.Core.Project.LanguageServerManager.SymbolCacheVersion, NullLogger.Instance);
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

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
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
