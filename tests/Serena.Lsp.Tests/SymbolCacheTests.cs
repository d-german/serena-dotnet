// Symbol Cache Tests - Phase 8

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp.Caching;

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
    public void TryGet_NormalizesBackslashesToForwardSlashes()
    {
        // Regression: indexer stores keys with forward slashes (from Matcher glob
        // output), but tool lookup goes through Path.GetFullPath which on Windows
        // produces backslashes. Without normalization the cache never hits on Windows.
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("C:/OnBase.NET/Libraries/Foo.cs", "fp1", "value");

        cache.TryGet("C:\\OnBase.NET\\Libraries\\Foo.cs", "fp1").Should().Be("value");
        cache.TryGetUnchecked("C:\\OnBase.NET\\Libraries\\Foo.cs").Should().Be("value");
    }

    [Fact]
    public void Set_NormalizesBackslashesToForwardSlashes()
    {
        var cache = new SymbolCache<string>(
            Path.GetTempPath(), "test_cache.json", 1, NullLogger.Instance);

        cache.Set("C:\\OnBase.NET\\Libraries\\Foo.cs", "fp1", "value");
        cache.TryGet("C:/OnBase.NET/Libraries/Foo.cs", "fp1").Should().Be("value");
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

            var cache2 = new SymbolCache<string>(cacheDir, "test.json", 42, NullLogger.Instance);
            cache2.Load().Should().BeTrue();
            cache2.TryGet("a.cs", "fp1").Should().Be("data1");
            cache2.TryGet("b.cs", "fp2").Should().Be("data2");
            cache2.Count.Should().Be(2);
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
}
