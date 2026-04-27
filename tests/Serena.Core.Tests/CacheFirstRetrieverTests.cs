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

    /// <summary>
    /// Regression for v1.0.30 bug: cache deserialization loses Parent links
    /// because <c>UnifiedSymbolInformation.Parent</c> is <c>[JsonIgnore]</c>.
    /// Class-qualified patterns like "ThumbnailController/GetPageThumbnail"
    /// must still match a method nested under that class on a file-scoped
    /// cache hit. <see cref="LanguageServerSymbol.FromUnified"/> relinks
    /// Parent on each child as it walks; this test verifies that.
    /// </summary>
    [Fact]
    public async Task FindSymbolsByNamePathAsync_FileScoped_MatchesClassQualifiedPattern_AfterParentLinksLost()
    {
        string root = Path.Combine(Path.GetTempPath(), "serena-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string relPath = "ThumbnailController.cs";
        string absPath = Path.Combine(root, relPath);
        File.WriteAllText(absPath, "class C {}");

        // Build a class-with-method tree, then strip Parent links to simulate
        // what happens after JSON round-trip through the on-disk cache.
        var range = new LspRange(new Position(0, 0), new Position(0, 0));
        var method = new UnifiedSymbolInformation
        {
            Name = "GetPageThumbnail",
            Kind = SymbolKind.Method,
            BodyRange = range,
            SelectionRange = range,
            Location = new Location("file:///test.cs", range),
        };
        var cls = new UnifiedSymbolInformation
        {
            Name = "ThumbnailController",
            Kind = SymbolKind.Class,
            BodyRange = range,
            SelectionRange = range,
            Location = new Location("file:///test.cs", range),
            Children = [method],
        };
        // DELIBERATELY do NOT set method.Parent = cls. This mirrors the
        // post-deserialization state that broke find_symbol pre-v1.0.30.

        var cache = NewEmptyCache();
        string fingerprint = CacheFingerprint.ForFile(absPath);
        cache.Set(absPath, fingerprint, [cls]);

        var retriever = new LanguageServerSymbolRetriever(
            _ => throw new InvalidOperationException("LSP must not be started"),
            root, NullLogger.Instance, cache);

        var matches = await retriever.FindSymbolsByNamePathAsync(
            "ThumbnailController/GetPageThumbnail", relPath);

        matches.Should().ContainSingle(s => s.Name == "GetPageThumbnail",
            "class-qualified pattern must match nested method even when Parent links were lost in cache");
    }

    /// <summary>
    /// v1.0.34: leaf-name fallback. Two distinct symbols with the same leaf
    /// name in different parents must both be returned by FindByLeafNameAsync,
    /// case-insensitively.
    /// </summary>
    [Fact]
    public async Task FindByLeafNameAsync_TwoSymbolsSameLeaf_BothReturned_CaseInsensitive()
    {
        string root = Path.Combine(Path.GetTempPath(), "serena-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string relPath = "Both.cs";
        string absPath = Path.Combine(root, relPath);
        File.WriteAllText(absPath, "class X {}");

        var range = new LspRange(new Position(0, 0), new Position(0, 0));
        UnifiedSymbolInformation MakeMethod(string name) => new()
        {
            Name = name,
            Kind = SymbolKind.Method,
            BodyRange = range,
            SelectionRange = range,
            Location = new Location("file:///test.cs", range),
        };

        var clsA = new UnifiedSymbolInformation
        {
            Name = "Alpha",
            Kind = SymbolKind.Class,
            BodyRange = range, SelectionRange = range,
            Location = new Location("file:///test.cs", range),
            Children = [MakeMethod("Run")],
        };
        var clsB = new UnifiedSymbolInformation
        {
            Name = "Beta",
            Kind = SymbolKind.Class,
            BodyRange = range, SelectionRange = range,
            Location = new Location("file:///test.cs", range),
            Children = [MakeMethod("Run")],
        };

        var cache = NewEmptyCache();
        string fingerprint = CacheFingerprint.ForFile(absPath);
        cache.Set(absPath, fingerprint, [clsA, clsB]);

        var retriever = new LanguageServerSymbolRetriever(
            _ => throw new InvalidOperationException("LSP must not be started"),
            root, NullLogger.Instance, cache);

        var matches = await retriever.FindByLeafNameAsync("run", relPath);

        matches.Should().HaveCount(2);
        matches.Should().OnlyContain(s => s.Name == "Run");
    }
}
