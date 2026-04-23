// Regression tests for the "cache first, LSP lazy" invariant.
//
// History: through v1.0.21 we repeatedly shipped fixes for slow read-only
// tools on large repos because every tool went through
// RequireSymbolRetrieverAsync, which unconditionally started the LSP before
// the cache was ever consulted. v1.0.22 fixed this by giving
// LanguageServerSymbolRetriever a lazy LSP factory: cache-hit paths never
// invoke the factory. This file guards that invariant with a retriever that
// would throw if the factory were ever called.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Editor;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Core.Tests;

public class CacheFirstRetrieverTests
{
    private static SymbolCache<UnifiedSymbolInformation[]> NewEmptyCache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "serena-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 1, NullLogger.Instance);
    }

    private static UnifiedSymbolInformation MakeSymbol(string name)
    {
        var range = new LspRange(new Position(0, 0), new Position(0, 0));
        return new UnifiedSymbolInformation
        {
            Name = name,
            Kind = SymbolKind.Class,
            BodyRange = range,
            SelectionRange = range,
            Location = new Location("file:///test.cs", range),
        };
    }

    [Fact]
    public async Task GetSymbolsAsync_CacheHit_DoesNotInvokeLspFactory()
    {
        // Arrange: populate cache with a fresh entry, and a factory that would
        // explode if ever called.
        string root = Path.Combine(Path.GetTempPath(), "serena-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string relPath = "a.cs";
        string absPath = Path.Combine(root, relPath);
        File.WriteAllText(absPath, "class A {}");

        var cache = NewEmptyCache();
        string fingerprint = CacheFingerprint.ForFile(absPath);
        cache.Set(absPath, fingerprint, [MakeSymbol("A")]);

        bool factoryCalled = false;
        Task<LspClient> ExplodingFactory(CancellationToken _)
        {
            factoryCalled = true;
            throw new InvalidOperationException("LSP factory must not be invoked on a cache hit");
        }

        var retriever = new LanguageServerSymbolRetriever(
            ExplodingFactory, root, NullLogger.Instance, cache);

        // Act
        var symbols = await retriever.GetSymbolsAsync(relPath);

        // Assert
        factoryCalled.Should().BeFalse("cache hit must not trigger LSP startup");
        symbols.Should().ContainSingle(s => s.Name == "A");
    }

    [Fact]
    public async Task FindSymbolsByNamePathAsync_ProjectWideCacheHit_DoesNotInvokeLspFactory()
    {
        // Arrange: populate cache with a symbol somewhere in the project.
        string root = Path.Combine(Path.GetTempPath(), "serena-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string absPath = Path.Combine(root, "sub", "b.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
        File.WriteAllText(absPath, "class B {}");

        var cache = NewEmptyCache();
        cache.Set(absPath, "fp", [MakeSymbol("B")]);

        bool factoryCalled = false;
        Task<LspClient> ExplodingFactory(CancellationToken _)
        {
            factoryCalled = true;
            throw new InvalidOperationException("LSP factory must not be invoked on a cache hit");
        }

        var retriever = new LanguageServerSymbolRetriever(
            ExplodingFactory, root, NullLogger.Instance, cache);

        // Act: project-wide search (relativePath = null).
        var matches = await retriever.FindSymbolsByNamePathAsync("B");

        // Assert
        factoryCalled.Should().BeFalse("project-wide find_symbol on a populated cache must not start LSP");
        matches.Should().ContainSingle(s => s.Name == "B");
    }

    [Fact]
    public async Task FindSymbolsByNamePathAsync_DirectoryScopedCacheHit_DoesNotInvokeLspFactory()
    {
        string root = Path.Combine(Path.GetTempPath(), "serena-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string subDir = Path.Combine(root, "sub");
        Directory.CreateDirectory(subDir);
        string absPath = Path.Combine(subDir, "c.cs");
        File.WriteAllText(absPath, "class C {}");

        var cache = NewEmptyCache();
        cache.Set(absPath, "fp", [MakeSymbol("C")]);

        bool factoryCalled = false;
        Task<LspClient> ExplodingFactory(CancellationToken _)
        {
            factoryCalled = true;
            throw new InvalidOperationException("LSP factory must not be invoked on a cache hit");
        }

        var retriever = new LanguageServerSymbolRetriever(
            ExplodingFactory, root, NullLogger.Instance, cache);

        // Act: directory-scoped search
        var matches = await retriever.FindSymbolsByNamePathAsync("C", "sub");

        // Assert
        factoryCalled.Should().BeFalse("directory-scoped find_symbol on a populated cache must not start LSP");
        matches.Should().ContainSingle(s => s.Name == "C");
    }
}
