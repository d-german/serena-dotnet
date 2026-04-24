// Regression tests for the "cache first, LSP lazy" invariant on the EDIT path.
//
// History: v1.0.22 added cache-first read-only retrievers. v1.0.23 extends
// the same invariant to symbol-edit tools (replace_symbol_body,
// insert_before_symbol, insert_after_symbol): when the symbol's range is
// already in the declarations cache, the splice runs as pure file I/O and
// the language server is never started. These tests pin the invariant with
// an editor whose lazy LSP factory throws if invoked.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Editor;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Core.Tests;

public class CacheFirstEditorTests
{
    private static SymbolCache<UnifiedSymbolInformation[]> NewEmptyCache()
    {
        string dir = Path.Combine(Path.GetTempPath(), "serena-edit-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 1, NullLogger.Instance);
    }

    private static UnifiedSymbolInformation MakeSymbol(string name)
    {
        // Body range that spans the entire single-line file. The actual
        // splice content is irrelevant to the invariant under test.
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

    private static (string root, string relPath, string absPath, SymbolCache<UnifiedSymbolInformation[]> cache)
        BuildPopulatedCache(string symbolName, string fileContent = "class A {}")
    {
        string root = Path.Combine(Path.GetTempPath(), "serena-edit-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string relPath = "a.cs";
        string absPath = Path.Combine(root, relPath);
        File.WriteAllText(absPath, fileContent);

        var cache = NewEmptyCache();
        string fingerprint = CacheFingerprint.ForFile(absPath);
        cache.Set(absPath, fingerprint, [MakeSymbol(symbolName)]);

        return (root, relPath, absPath, cache);
    }

    private static (LanguageServerCodeEditor editor, Func<bool> wasFactoryCalled) BuildExplodingEditor(
        string root,
        SymbolCache<UnifiedSymbolInformation[]> cache)
    {
        bool factoryCalled = false;
        Task<LspClient> ExplodingFactory(CancellationToken _)
        {
            factoryCalled = true;
            throw new InvalidOperationException("LSP factory must not be invoked on a cache hit");
        }

        var retriever = new LanguageServerSymbolRetriever(
            ExplodingFactory, root, NullLogger.Instance, cache);
        var editor = new LanguageServerCodeEditor(
            retriever, ExplodingFactory, root, NullLogger.Instance);

        return (editor, () => factoryCalled);
    }

    [Fact]
    public async Task ReplaceSymbolBody_CacheHit_DoesNotInvokeLspFactory()
    {
        var (root, relPath, _, cache) = BuildPopulatedCache("A");
        var (editor, wasCalled) = BuildExplodingEditor(root, cache);

        var result = await editor.ReplaceSymbolBodyAsync("A", relPath, "new body", CancellationToken.None);

        wasCalled().Should().BeFalse("cache-hit replace_symbol_body must not start LSP");
        result.Should().Contain("Replaced body");
    }

    [Fact]
    public async Task InsertBefore_CacheHit_DoesNotInvokeLspFactory()
    {
        var (root, relPath, _, cache) = BuildPopulatedCache("A");
        var (editor, wasCalled) = BuildExplodingEditor(root, cache);

        var result = await editor.InsertBeforeSymbolAsync("A", relPath, "// inserted", CancellationToken.None);

        wasCalled().Should().BeFalse("cache-hit insert_before_symbol must not start LSP");
        result.Should().Contain("Inserted content before");
    }

    [Fact]
    public async Task InsertAfter_CacheHit_DoesNotInvokeLspFactory()
    {
        var (root, relPath, _, cache) = BuildPopulatedCache("A");
        var (editor, wasCalled) = BuildExplodingEditor(root, cache);

        var result = await editor.InsertAfterSymbolAsync("A", relPath, "// inserted", CancellationToken.None);

        wasCalled().Should().BeFalse("cache-hit insert_after_symbol must not start LSP");
        result.Should().Contain("Inserted content after");
    }
}
