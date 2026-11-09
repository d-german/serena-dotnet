// Outlines dropped by an edit are rebuilt in the background, retried while the
// server is unusable, and given up on after a few failures.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Project;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.LanguageServers;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Core.Tests;

public sealed class OutlinePendingQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "serena_outline_" + Guid.NewGuid().ToString("N"));
    private readonly LanguageServerManager _manager;

    public OutlinePendingQueueTests()
    {
        Directory.CreateDirectory(_root);
        _manager = new LanguageServerManager(_root, new LanguageServerRegistry(), NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task CSharpEdit_RebuildsTheOutlineAtOnce_WithoutAnyServer()
    {
        string file = Path.Combine(_root, "A.cs");
        const string text = "namespace Demo;\nclass Alpha { Beta Field; void Run() { } }\n";
        await File.WriteAllTextAsync(file, text);

        var update = await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.CSharp, file, text);

        update.Outline.Should().Be(OutlineRefresh.Refreshed);
        update.OccurrencesRefreshed.Should().BeTrue();
        update.ServerState.Should().Be(WorkspaceReadyState.NotStarted);
        _manager.PendingOutlineCount(Language.CSharp).Should().Be(0);
        var cache = _manager.GetSymbolCache(Language.CSharp)!;
        var outline = cache.TryGet(file, CacheFingerprint.ForFile(file));
        outline.Should().NotBeNull();
        outline![0].Children.Single().Children.Select(c => c.Name).Should().Equal("Field", "Run");
        cache.Occurrences.Query("Beta", null).Should().ContainSingle();
    }

    // The languages below still rely on a server for their outline, so they exercise the pending set.

    [Fact]
    public async Task EditWithoutServer_DefersTheOutline_AndKeepsOccurrencesCurrent()
    {
        string file = Path.Combine(_root, "a.ts");
        await File.WriteAllTextAsync(file, "class Alpha { b: Beta; }\n");

        var update = await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.TypeScript, file, "class Alpha { b: Beta; }\n");

        update.Outline.Should().Be(OutlineRefresh.Deferred);
        update.OccurrencesRefreshed.Should().BeTrue();
        update.ServerState.Should().Be(WorkspaceReadyState.NotStarted);
        _manager.PendingOutlineCount(Language.TypeScript).Should().Be(1);
        _manager.GetSymbolCache(Language.TypeScript)!.Occurrences.Query("Beta", null).Should().ContainSingle();
    }

    [Fact]
    public async Task Drain_RebuildsPendingOutlines_AndRetriesFailuresUpToTheLimit()
    {
        string good = Path.Combine(_root, "good.ts");
        string bad = Path.Combine(_root, "bad.ts");
        await File.WriteAllTextAsync(good, "class Good { }\n");
        await File.WriteAllTextAsync(bad, "class Bad { }\n");
        await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.TypeScript, good, "class Good { }\n");
        await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.TypeScript, bad, "class Bad { }\n");
        var cache = _manager.GetSymbolCache(Language.TypeScript)!;

        int attemptsOnBad = 0;
        Task<ReindexOutcome> Reindex(string path, CancellationToken ct)
        {
            if (path.EndsWith("bad.ts", StringComparison.OrdinalIgnoreCase))
            {
                attemptsOnBad++;
                return Task.FromResult(new ReindexOutcome(false, "document symbol request timed out after 60s"));
            }
            cache.Set(path, CacheFingerprint.ForFile(path), [Symbol("Good")]);
            return Task.FromResult(ReindexOutcome.Done);
        }

        (await _manager.DrainPendingOutlinesAsync(Language.TypeScript, Reindex, CancellationToken.None)).Should().Be(1);
        _manager.PendingOutlineCount(Language.TypeScript).Should().Be(1, "the failing file stays queued");
        cache.TryGetUnchecked(good).Should().NotBeNull();
        _manager.LastOutlineFailure(Language.TypeScript).Should().Contain("bad.ts: document symbol request timed out after 60s (attempt 1 of 3)");

        await _manager.DrainPendingOutlinesAsync(Language.TypeScript, Reindex, CancellationToken.None);
        await _manager.DrainPendingOutlinesAsync(Language.TypeScript, Reindex, CancellationToken.None);

        attemptsOnBad.Should().Be(LanguageServerManager.MaxOutlineAttempts);
        _manager.PendingOutlineCount(Language.TypeScript).Should().Be(0, "a file that keeps failing is given up on until the next index");
    }

    [Fact]
    public async Task Drain_DropsAnOutlineOfTextThatChangedWhileItWasRequested()
    {
        string file = Path.Combine(_root, "racy.ts");
        await File.WriteAllTextAsync(file, "class Racy { }\n");
        await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.TypeScript, file, "class Racy { }\n");
        var cache = _manager.GetSymbolCache(Language.TypeScript)!;

        int calls = 0;
        Task<ReindexOutcome> Reindex(string path, CancellationToken ct)
        {
            calls++;
            cache.Set(path, CacheFingerprint.ForFile(path), [Symbol("Racy")]);
            if (calls == 1)
            {
                // Edited again while the server was answering.
                File.WriteAllText(path, "class Racy { s: Sigma; }\n");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            }
            return Task.FromResult(ReindexOutcome.Done);
        }

        int rebuilt = await _manager.DrainPendingOutlinesAsync(Language.TypeScript, Reindex, CancellationToken.None);

        rebuilt.Should().Be(1, "the first outline described old text and was dropped; the second pass rebuilt it");
        calls.Should().Be(2);
        _manager.PendingOutlineCount(Language.TypeScript).Should().Be(0);
        cache.Occurrences.Query("Sigma", null).Should().ContainSingle("the occurrence rows follow the newer text");
    }

    [Fact]
    public async Task Drain_ForgetsAFileDeletedWhileItWasPending()
    {
        string file = Path.Combine(_root, "gone.ts");
        await File.WriteAllTextAsync(file, "class Gone { }\n");
        await _manager.UpdateSymbolCacheForWrittenFileAsync(Language.TypeScript, file, "class Gone { }\n");
        File.Delete(file);

        int rebuilt = await _manager.DrainPendingOutlinesAsync(
            Language.TypeScript, (_, _) => Task.FromResult(ReindexOutcome.Done), CancellationToken.None);

        rebuilt.Should().Be(0);
        _manager.PendingOutlineCount(Language.TypeScript).Should().Be(0);
    }

    public void Dispose()
    {
        _manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static UnifiedSymbolInformation Symbol(string name) => new()
    {
        Name = name,
        Kind = SymbolKind.Class,
        BodyRange = new LspRange(new Position(0, 0), new Position(0, 1)),
    };
}
