// Identifier occurrence extraction and its persistence in the symbol cache.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

public class OccurrenceIndexTests
{
    [Fact]
    public void Extract_KeepsPascalCaseIdentifiers_WithOneBasedLines_AndSkipsLocalsAndKeywords()
    {
        const string source = """
            using System;
            public class PatientRepository
            {
                private readonly ILogger _logger;
                public Result<Patient> Go(int id)
                {
                    var patient = PatientRepository.Load(id);
                    return patient;
                }
            }
            """;

        var occurrences = OccurrenceIndexer.Extract(source);

        occurrences["PatientRepository"].Should().Equal(2, 7);
        occurrences["Result"].Should().Equal(5);
        occurrences["Patient"].Should().Equal(5);
        occurrences["ILogger"].Should().Equal(4);
        occurrences.Should().NotContainKey("patient", "locals are not indexed");
        occurrences.Should().NotContainKey("_logger", "private fields are not indexed");
        occurrences.Should().NotContainKey("public", "keywords are not indexed");
        occurrences.Should().NotContainKey("id");
        occurrences.Should().NotContainKey("Go", "names shorter than three characters are not indexed");
    }

    [Fact]
    public void Extract_MasksComments_ButKeepsStringLiterals()
    {
        const string source = "// Alpha in a line comment\n" +
                              "/* Beta in a\n block comment */\n" +
                              "var url = \"https://Gamma.example/Delta\"; // Epsilon\n" +
                              "var raw = \"\"\"Zeta // not a comment\"\"\";\n";

        var occurrences = OccurrenceIndexer.Extract(source);

        occurrences.Should().NotContainKey("Alpha");
        occurrences.Should().NotContainKey("Beta");
        occurrences.Should().NotContainKey("Epsilon");
        occurrences["Gamma"].Should().Equal(4);
        occurrences["Delta"].Should().Equal(4);
        occurrences["Zeta"].Should().Equal(5);
    }

    [Fact]
    public void MaskComments_PreservesLineCount()
    {
        const string source = "A\n/* x\n y */B\n// z\nC";
        OccurrenceIndexer.MaskComments(source).Count(c => c == '\n').Should().Be(source.Count(c => c == '\n'));
    }

    [Fact]
    public void Cache_PersistsOccurrences_AndQueriesThemByName_AndRemovesWithEntry()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string fileA = Path.Combine(dir, "A.cs");
            string fileB = Path.Combine(dir, "B.cs");
            File.WriteAllText(fileA, "class Alpha { Beta b; }\nclass Other { Beta c; }\n");
            File.WriteAllText(fileB, "class Beta { }\n");

            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(fileA, "fp-a", [Symbol("Alpha", 0, 0), Symbol("Other", 1, 1)]);
            cache.Set(fileB, "fp-b", [Symbol("Beta", 0, 0)]);
            cache.Save();

            var reopened = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            reopened.Load().Should().BeTrue();
            reopened.Occurrences.RowCount().Should().BeGreaterThan(0);

            var hits = reopened.Occurrences.Query("Beta", null);
            hits.Should().HaveCount(2);
            hits.Single(h => h.Path.EndsWith("A.cs", StringComparison.Ordinal)).Lines.Should().Equal(1, 2);
            hits.Single(h => h.Path.EndsWith("B.cs", StringComparison.Ordinal)).Lines.Should().Equal(1);

            reopened.Occurrences.Query("beta", dir).Should().HaveCount(2, "lookup is case-insensitive and the prefix matches the directory");
            reopened.Occurrences.Query("Beta", Path.Combine(dir, "nowhere")).Should().BeEmpty();

            reopened.Remove(fileA);
            reopened.Save();
            reopened.Occurrences.Query("Beta", null).Should().ContainSingle(h => h.Path.EndsWith("B.cs", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void EditWithoutLanguageServer_RemovesOutline_ButRefreshesOccurrencesFromText()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, "class Alpha { Beta b; }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(file, "fp-1", [Symbol("Alpha", 0, 0)]);
            cache.Save();

            // The edit tools do this when no language server is running:
            // drop the outline (it needs Roslyn) and rebuild occurrences from text.
            File.WriteAllText(file, "class Alpha { Gamma g; }\nclass Delta { }\n");
            cache.Remove(file);
            cache.Occurrences.Capture(file);
            cache.Save();

            var reopened = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            reopened.Load().Should().BeTrue();
            reopened.TryGetUnchecked(file).Should().BeNull("the stale outline must not be served");
            reopened.Occurrences.Query("Beta", null).Should().BeEmpty("the old text's identifiers are gone");
            reopened.Occurrences.Query("Gamma", null).Should().ContainSingle().Which.Lines.Should().Equal(1);
            reopened.Occurrences.Query("Delta", null).Should().ContainSingle().Which.Lines.Should().Equal(2);

            // A later outline refresh (server available again) keeps the rows consistent.
            reopened.Set(file, "fp-2", [Symbol("Alpha", 0, 0), Symbol("Delta", 1, 1)]);
            reopened.Save();
            reopened.Occurrences.Query("Delta", null).Should().ContainSingle();
            reopened.TryGetUnchecked(file).Should().NotBeNull();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DeletedFile_LosesItsOccurrenceRows()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "Gone.cs");
            File.WriteAllText(file, "class Omega { }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Set(file, "fp", [Symbol("Omega", 0, 0)]);
            cache.Save();

            File.Delete(file);
            cache.Remove(file);
            cache.Occurrences.Capture(file); // no-op for a missing file
            cache.Save();

            cache.Occurrences.Query("Omega", null).Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void PreReleaseEntryKeyedOccurrenceTable_IsMigratedToPathKeys()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "Old.cs");
            File.WriteAllText(file, "class Sigma { Tau t; }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Occurrences.Enabled = false;
            cache.Set(file, "fp", [Symbol("Sigma", 0, 0)]);
            cache.Save();

            // Rewrite the table in the pre-release shape.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "symbols.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX IF EXISTS ix_occurrences_name;
                    DROP TABLE occurrences;
                    CREATE TABLE occurrences(
                        entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                        name TEXT NOT NULL COLLATE NOCASE,
                        lines TEXT NOT NULL,
                        PRIMARY KEY(entry_id, name));
                    INSERT INTO occurrences(entry_id, name, lines) SELECT id, 'Tau', '1' FROM entries;
                    """;
                command.ExecuteNonQuery();
            }

            var reopened = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            reopened.Load().Should().BeTrue();
            reopened.Occurrences.Query("Tau", null).Should().ContainSingle()
                .Which.Path.Should().EndWith("Old.cs");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void PreReleasePathKeyedOccurrenceTable_IsMigratedToPathIds()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "Mid.cs");
            File.WriteAllText(file, "class Rho { Phi p; }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Occurrences.Enabled = false;
            cache.Set(file, "fp", [Symbol("Rho", 0, 0)]);
            cache.Save();

            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "symbols.db")};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DROP INDEX IF EXISTS ix_occurrences_name;
                    DROP TABLE occurrences;
                    DROP TABLE occurrence_paths;
                    CREATE TABLE occurrences(
                        path TEXT NOT NULL COLLATE NOCASE,
                        name TEXT NOT NULL COLLATE NOCASE,
                        lines TEXT NOT NULL,
                        PRIMARY KEY(path, name));
                    INSERT INTO occurrences(path, name, lines) SELECT path, 'Phi', '1' FROM entries;
                    """;
                command.ExecuteNonQuery();
            }

            var reopened = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            reopened.Load().Should().BeTrue();
            reopened.Occurrences.Query("Phi", null).Should().ContainSingle()
                .Which.Path.Should().EndWith("Mid.cs");
            reopened.Occurrences.RowCount().Should().Be(1);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Backfill_AddsOccurrencesForCachedFilesThatHaveNone_AndSkipsCoveredOnes()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string a = Path.Combine(dir, "A.cs");
            string b = Path.Combine(dir, "B.cs");
            File.WriteAllText(a, "class Alpha { }\n");
            File.WriteAllText(b, "class Beta { Alpha a; }\n");

            // Simulate an index built before occurrence capture: outlines only.
            var old = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            old.Occurrences.Enabled = false;
            old.Set(a, "fp-a", [Symbol("Alpha", 0, 0)]);
            old.Set(b, "fp-b", [Symbol("Beta", 0, 0)]);
            old.Save();

            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Load().Should().BeTrue();
            cache.Occurrences.RowCount().Should().Be(0);

            cache.Occurrences.Backfill([a, b]).Should().Be(2);
            cache.Occurrences.Query("Alpha", null).Should().HaveCount(2);

            cache.Occurrences.Backfill([a, b]).Should().Be(0, "files that already have rows are skipped");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Cache_WithOccurrencesDisabled_StoresNone()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"serena_occ_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "A.cs");
            File.WriteAllText(file, "class Alpha { }\n");
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(dir, "symbols.json", 2, NullLogger.Instance);
            cache.Occurrences.Enabled = false;
            cache.Set(file, "fp", [Symbol("Alpha", 0, 0)]);
            cache.Save();
            cache.Occurrences.RowCount().Should().Be(0);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static UnifiedSymbolInformation Symbol(string name, int startLine, int endLine) => new()
    {
        Name = name,
        Kind = SymbolKind.Class,
        BodyRange = new Protocol.Types.Range(new Position(startLine, 0), new Position(endLine, 1)),
    };
}
