// The index keeping up with files changed, created or deleted by something
// other than serena's own edit tools (a shell, an IDE, git).

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

public class CacheReconciliationTests
{
    [Fact]
    public void ReconcileWithDisk_DropsDeleted_RecapturesChanged_AndAddsNewFilesInKnownDirectories()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_rec_{Guid.NewGuid():N}");
        string src = Path.Combine(dir, "src");
        string elsewhere = Path.Combine(dir, "elsewhere");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(elsewhere);
        try
        {
            string deleted = Path.Combine(src, "Deleted.cs");
            string changed = Path.Combine(src, "Changed.cs");
            string untouched = Path.Combine(src, "Untouched.cs");
            string created = Path.Combine(src, "Created.cs");
            string outside = Path.Combine(elsewhere, "Outside.cs");
            File.WriteAllText(deleted, "class Alpha { }\n");
            File.WriteAllText(changed, "class Beta { }\n");
            File.WriteAllText(untouched, "class Gamma { }\n");

            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(deleted, CacheFingerprint.ForFile(deleted), [Symbol("Alpha")]);
            cache.Set(changed, CacheFingerprint.ForFile(changed), [Symbol("Beta")]);
            cache.Set(untouched, CacheFingerprint.ForFile(untouched), [Symbol("Gamma")]);
            cache.Save();

            // Outside serena: one file deleted, one rewritten, one created next to
            // the indexed ones, one created in a folder the index never covered.
            File.Delete(deleted);
            File.WriteAllText(changed, "class Delta { Epsilon e; }\n");
            File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(created, "class Zeta { }\n");
            File.WriteAllText(outside, "class Eta { }\n");

            var reopened = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            reopened.Load().Should().BeTrue();
            var result = reopened.ReconcileWithDisk([changed, untouched, created, outside]);

            result.Known.Should().Be(3);
            result.Removed.Should().Be(1, "Deleted.cs is gone");
            result.Refreshed.Should().Be(2, "Changed.cs was rewritten and Created.cs is new in an indexed folder");

            reopened.TryGetUnchecked(deleted).Should().BeNull();
            reopened.Occurrences.Query("Alpha", null).Should().BeEmpty();
            reopened.TryGetUnchecked(changed).Should().BeNull("a changed outline needs the language server to rebuild");
            reopened.Occurrences.Query("Beta", null).Should().BeEmpty("the old text's identifiers are gone");
            reopened.Occurrences.Query("Epsilon", null).Should().ContainSingle();
            reopened.Occurrences.Query("Zeta", null).Should().ContainSingle();
            reopened.Occurrences.Query("Eta", null).Should().BeEmpty("a folder the index never covered stays out of scope");
            reopened.TryGet(untouched, CacheFingerprint.ForFile(untouched)).Should().NotBeNull("an unchanged file keeps its outline");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Query_LeavesOutDeletedFiles_AndDropsTheirRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string kept = Path.Combine(dir, "Kept.cs");
            string gone = Path.Combine(dir, "Gone.cs");
            File.WriteAllText(kept, "class Theta { Iota i; }\n");
            File.WriteAllText(gone, "class Kappa { Iota i; }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(kept, "fp-k", [Symbol("Theta")]);
            cache.Set(gone, "fp-g", [Symbol("Kappa")]);
            cache.Save();

            File.Delete(gone);

            cache.Occurrences.Query("Iota", null).Should().ContainSingle().Which.Path.Should().EndWith("Kept.cs");
            cache.Save();
            cache.Occurrences.Paths().Should().NotContain(p => p.EndsWith("Gone.cs", StringComparison.Ordinal));
            cache.TryGetUnchecked(gone).Should().BeNull("the deleted file's outline goes with its rows");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void PruneMissingFiles_RemovesEveryFileThatIsGone()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_rec_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "A.cs");
            string b = Path.Combine(dir, "B.cs");
            File.WriteAllText(a, "class Lambda { }\n");
            File.WriteAllText(b, "class Mu { }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(a, "fp-a", [Symbol("Lambda")]);
            cache.Set(b, "fp-b", [Symbol("Mu")]);
            cache.Save();

            File.Delete(a);
            File.Delete(b);

            cache.PruneMissingFiles().Should().Be(2);
            cache.Save();
            cache.Count.Should().Be(0);
            cache.Occurrences.RowCount().Should().Be(0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Remove_OfUnknownFile_DoesNotCreateADatabase()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_rec_{Guid.NewGuid():N}");
        try
        {
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Remove(Path.Combine(dir, "Never.cs"));
            cache.Save();
            Directory.Exists(dir).Should().BeFalse("an edit in a project that was never indexed must not start an index");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static UnifiedSymbolInformation Symbol(string name) => new()
    {
        Name = name,
        Kind = SymbolKind.Class,
        BodyRange = new Protocol.Types.Range(new Position(0, 0), new Position(0, 1)),
    };
}
