// Symbol Cache Tests - Phase 8

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

public class SymbolCacheTests
{
    [Fact]
    public void TryGet_ReturnsNull_OnMiss()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.TryGet("file.cs", "abc123").Should().BeNull();
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsValue()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("file.cs", "abc123", "cached data");
        cache.TryGet("file.cs", "abc123").Should().Be("cached data");
    }

    [Fact]
    public void TryGet_ReturnsNull_OnStaleFingerprint()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("file.cs", "abc123", "old data");
        cache.TryGet("file.cs", "def456").Should().BeNull();
    }

    [Fact]
    public void Remove_ClearsEntry()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("file.cs", "abc123", "data");
        cache.Remove("file.cs");
        cache.TryGet("file.cs", "abc123").Should().BeNull();
    }

    [Fact]
    public void Remove_PersistsInvalidationWithoutFullCacheSave()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            var cache1 = new SymbolCache<string>(cacheDir, "test.json", 1, NullLogger.Instance);
            cache1.Set("a.cs", "fp1", "old a");
            cache1.Set("b.cs", "fp2", "still valid");
            cache1.Save();

            var cache2 = new SymbolCache<string>(cacheDir, "test.json", 1, NullLogger.Instance);
            cache2.Load().Should().BeTrue();
            cache2.Remove("a.cs");

            var cache3 = new SymbolCache<string>(cacheDir, "test.json", 1, NullLogger.Instance);
            cache3.Load().Should().BeTrue();

            cache3.TryGetUnchecked("a.cs").Should().BeNull(
                "a write must not resurrect stale symbols if the MCP process restarts before the full cache is saved");
            cache3.TryGet("b.cs", "fp2").Should().Be("still valid");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void TryGet_NormalizesBackslashesToForwardSlashes()
    {
        // Regression: indexer stores keys with forward slashes (from Matcher glob
        // output), but tool lookup goes through Path.GetFullPath which on Windows
        // produces backslashes. Without normalization the cache never hits on Windows.
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("C:/BigRepo/Libraries/Foo.cs", "fp1", "value");

        cache.TryGet("C:\\BigRepo\\Libraries\\Foo.cs", "fp1").Should().Be("value");
        cache.TryGetUnchecked("C:\\BigRepo\\Libraries\\Foo.cs").Should().Be("value");
    }

    [Fact]
    public void Set_NormalizesBackslashesToForwardSlashes()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("C:\\BigRepo\\Libraries\\Foo.cs", "fp1", "value");
        cache.TryGet("C:/BigRepo/Libraries/Foo.cs", "fp1").Should().Be("value");
    }

    [Fact]
    public void Load_CollapsesDuplicateKeysAfterPathNormalization()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(cacheDir);
            string cacheJson = """
                {
                  "version": 1,
                  "entries": {
                    "C:/BigRepo/Libraries/Foo.cs": { "fingerprint": "old", "data": "old data" },
                    "C:\\BigRepo\\Libraries\\Foo.cs": { "fingerprint": "new", "data": "new data" }
                  }
                }
                """;
            File.WriteAllText(Path.Combine(cacheDir, "test.json"), cacheJson);

            var cache = new SymbolCache<string>(cacheDir, "test.json", 1, NullLogger.Instance);

            cache.Load().Should().BeTrue();
            cache.Count.Should().Be(1);
            cache.TryGet("C:/BigRepo/Libraries/Foo.cs", "new").Should().Be("new data");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            var cache1 = new SymbolCache<string>(cacheDir, "test.json", 42, NullLogger.Instance);
            cache1.Set("a.cs", "fp1", "data1");
            cache1.Set("b.cs", "fp2", "data2");
            cache1.Save();

            File.Exists(Path.Combine(cacheDir, "test.db")).Should().BeTrue();
            File.Exists(Path.Combine(cacheDir, "test.json")).Should().BeFalse();

            var cache2 = new SymbolCache<string>(cacheDir, "test.json", 42, NullLogger.Instance);
            cache2.Load().Should().BeTrue();
            cache2.TryGet("a.cs", "fp1").Should().Be("data1");
            cache2.TryGet("b.cs", "fp2").Should().Be("data2");
            cache2.Count.Should().Be(2);
            cache2.GetFreshPaths(
            [
                ("a.cs", "fp1"),
                ("b.cs", "stale"),
                ("missing.cs", "fp3")
            ]).Should().BeEquivalentTo("a.cs");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void SymbolNameIndex_ReturnsOnlyCandidateFiles_AndHonorsScope()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            var cache = new SymbolCache<UnifiedSymbolInformation[]>(
                cacheDir, "symbols.json", 1, NullLogger.Instance);
            cache.Set("C:/repo/WebApp/CorrectionController.cs", "fp1",
            [
                Symbol("CorrectionController",
                    Symbol("FlagForCorrection"),
                    Symbol("ClearCorrectionFlag"))
            ]);
            cache.Set("C:/repo/Other/FlagService.cs", "fp2",
            [
                Symbol("FlagService", Symbol("FlagForCorrection"))
            ]);
            cache.Set("C:/repo/Other/Unrelated.cs", "fp3", [Symbol("Unrelated")]);
            cache.Save();

            cache.TryGetCandidatePaths(
                "FlagForCorrection", substringMatching: false,
                "C:/repo/WebApp", out var exact).Should().BeTrue();
            exact.Should().Equal("C:/repo/WebApp/CorrectionController.cs");

            cache.TryGetCandidatePaths(
                "Correction", substringMatching: true,
                pathPrefix: null, out var substring).Should().BeTrue();
            substring.Should().BeEquivalentTo(
                "C:/repo/WebApp/CorrectionController.cs",
                "C:/repo/Other/FlagService.cs");
            substring.Should().NotContain("C:/repo/Other/Unrelated.cs");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void Load_LegacyJson_MigratesToIndexedDatabase()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(cacheDir);
            var legacy = new CacheFile<UnifiedSymbolInformation[]>(1, new()
            {
                ["C:/repo/Foo.cs"] = new("fp", [Symbol("Foo")])
            });
            File.WriteAllText(
                Path.Combine(cacheDir, "symbols.json"),
                System.Text.Json.JsonSerializer.Serialize(legacy, CacheJsonOptionsForTest));

            var cache = new SymbolCache<UnifiedSymbolInformation[]>(
                cacheDir, "symbols.json", 1, NullLogger.Instance);

            cache.Load().Should().BeTrue();
            File.Exists(Path.Combine(cacheDir, "symbols.db")).Should().BeTrue();
            File.Exists(Path.Combine(cacheDir, "symbols.json")).Should().BeFalse();
            cache.TryGetCandidatePaths("Foo", false, null, out var candidates).Should().BeTrue();
            candidates.Should().Equal("C:/repo/Foo.cs");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void Load_PathIndexedDatabase_NormalizesSymbolNamesToEntryIds()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(cacheDir);
            string databasePath = Path.Combine(cacheDir, "symbols.db");
            string data = System.Text.Json.JsonSerializer.Serialize(
                new[] { Symbol("Foo") }, CacheJsonOptionsForTest);
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                    CREATE TABLE entries(
                        path TEXT PRIMARY KEY COLLATE NOCASE,
                        fingerprint TEXT NOT NULL,
                        data TEXT NOT NULL);
                    CREATE TABLE symbol_names(
                        path TEXT NOT NULL COLLATE NOCASE,
                        leaf_name TEXT NOT NULL COLLATE NOCASE,
                        PRIMARY KEY(path, leaf_name));
                    CREATE INDEX ix_symbol_names_leaf ON symbol_names(leaf_name COLLATE NOCASE);
                    INSERT INTO metadata(key, value) VALUES('cache_version', '1');
                    INSERT INTO entries(path, fingerprint, data) VALUES('C:/repo/Foo.cs', 'fp', $data);
                    INSERT INTO symbol_names(path, leaf_name) VALUES('C:/repo/Foo.cs', 'Foo');
                    """;
                command.Parameters.AddWithValue("$data", data);
                command.ExecuteNonQuery();
            }

            var cache = new SymbolCache<UnifiedSymbolInformation[]>(
                cacheDir, "symbols.json", 1, NullLogger.Instance);

            cache.Load().Should().BeTrue();
            cache.TryGetCandidatePaths("Foo", false, null, out var candidates).Should().BeTrue();
            candidates.Should().Equal("C:/repo/Foo.cs");

            using var migrated = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            migrated.Open();
            using var schema = migrated.CreateCommand();
            schema.CommandText = "PRAGMA table_info(symbol_names);";
            using var reader = schema.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
            columns.Should().Contain("entry_id");
            columns.Should().NotContain("path");
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void Load_ReturnsFalse_OnVersionMismatch()
    {
        string cacheDir = Path.Combine(Path.GetTempPath(), $"serena_test_{Guid.NewGuid():N}");
        try
        {
            var cache1 = new SymbolCache<string>(cacheDir, "test.json", 1, NullLogger.Instance);
            cache1.Set("a.cs", "fp1", "data1");
            cache1.Save();

            var cache2 = new SymbolCache<string>(cacheDir, "test.json", 2, NullLogger.Instance);
            cache2.Load().Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
            }
        }
    }

    [Fact]
    public void CacheFingerprint_ForContent_Deterministic()
    {
        var fp1 = CacheFingerprint.ForContent("hello world");
        var fp2 = CacheFingerprint.ForContent("hello world");
        fp1.Should().Be(fp2);
        fp1.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CacheFingerprint_ForContent_DifferentInputs()
    {
        var fp1 = CacheFingerprint.ForContent("hello");
        var fp2 = CacheFingerprint.ForContent("world");
        fp1.Should().NotBe(fp2);
    }

    private static UnifiedSymbolInformation Symbol(
        string name, params UnifiedSymbolInformation[] children) => new()
    {
        Name = name,
        Kind = SymbolKind.Class,
        Children = children.ToList(),
    };

    private static readonly System.Text.Json.JsonSerializerOptions CacheJsonOptionsForTest = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
}
