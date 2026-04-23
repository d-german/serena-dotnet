// Symbol Caching Subsystem - Ported from solidlsp/ls.py cache methods + solidlsp/util/cache.py
// Phase 3C: Cache persistence, fingerprinting, invalidation

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Serena.Lsp.Caching;

/// <summary>
/// Versioned, fingerprinted cache for document symbols.
/// Uses JSON serialization instead of Python's pickle.
/// </summary>
public sealed class SymbolCache<T>
{
    private readonly string _cacheDir;
    private readonly string _cacheFilename;
    private readonly int _cacheVersion;
    private readonly ILogger _logger;

    private ConcurrentDictionary<string, CacheEntry<T>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isModified;

    public SymbolCache(string cacheDir, string cacheFilename, int cacheVersion, ILogger logger)
    {
        _cacheDir = cacheDir;
        _cacheFilename = cacheFilename;
        _cacheVersion = cacheVersion;
        _logger = logger;
    }

    /// <summary>
    /// Tries to get a cached value for the given file path.
    /// Returns null if the cache entry is missing or stale.
    /// </summary>
    public T? TryGet(string filePath, string currentFingerprint)
    {
        if (_entries.TryGetValue(NormalizeKey(filePath), out var entry)
            && entry.Fingerprint == currentFingerprint)
        {
            return entry.Data;
        }
        return default;
    }

    /// <summary>
    /// Returns the cached value WITHOUT validating the fingerprint.
    /// Use only for bulk operations where stale data is acceptable
    /// (e.g., project-wide symbol search where false positives are tolerable).
    /// </summary>
    public T? TryGetUnchecked(string filePath)
    {
        if (_entries.TryGetValue(NormalizeKey(filePath), out var entry))
        {
            return entry.Data;
        }
        return default;
    }

    /// <summary>
    /// Stores a value in the cache.
    /// </summary>
    public void Set(string filePath, string fingerprint, T data)
    {
        _entries[NormalizeKey(filePath)] = new CacheEntry<T>(fingerprint, data);
        _isModified = true;
    }

    /// <summary>
    /// Removes a cached entry.
    /// </summary>
    public void Remove(string filePath)
    {
        if (_entries.TryRemove(NormalizeKey(filePath), out _))
        {
            _isModified = true;
        }
    }

    /// <summary>
    /// Normalizes a file path for use as a cache key. Delegates to
    /// <see cref="SymbolCacheKeys.Normalize"/> so the normalization rule lives
    /// in exactly one (non-generic) place.
    /// </summary>
    public static string NormalizeKey(string filePath) => SymbolCacheKeys.Normalize(filePath);

    /// <summary>
    /// Saves the cache to disk if modified.
    /// </summary>
    public void Save()
    {
        if (!_isModified)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_cacheDir);
            string path = Path.Combine(_cacheDir, _cacheFilename);
            var wrapper = new CacheFile<T>(_cacheVersion, new Dictionary<string, CacheEntry<T>>(_entries));
            string json = JsonSerializer.Serialize(wrapper, CacheJsonOptions.Default);
            File.WriteAllText(path, json, Encoding.UTF8);
            _isModified = false;
            _logger.LogDebug("Saved cache {File} ({Count} entries)", _cacheFilename, _entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save cache {File}", _cacheFilename);
        }
    }

    /// <summary>
    /// Loads the cache from disk. Returns false if cache is missing or has wrong version.
    /// </summary>
    public bool Load()
    {
        string path = Path.Combine(_cacheDir, _cacheFilename);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            var wrapper = JsonSerializer.Deserialize<CacheFile<T>>(json, CacheJsonOptions.Default);
            if (wrapper is null || wrapper.Version != _cacheVersion)
            {
                _logger.LogInformation("Cache version mismatch for {File}, rebuilding", _cacheFilename);
                return false;
            }

            _entries = new ConcurrentDictionary<string, CacheEntry<T>>(
                wrapper.Entries.Select(kv =>
                    new KeyValuePair<string, CacheEntry<T>>(NormalizeKey(kv.Key), kv.Value)),
                StringComparer.OrdinalIgnoreCase);
            _isModified = false;
            _logger.LogDebug("Loaded cache {File} ({Count} entries)", _cacheFilename, _entries.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cache {File}", _cacheFilename);
            return false;
        }
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Returns a snapshot of all cached file paths.
    /// </summary>
    public IReadOnlyCollection<string> Keys => [.. _entries.Keys];
}

/// <summary>
/// A single cached entry with its fingerprint for staleness detection.
/// </summary>
public sealed record CacheEntry<T>(string Fingerprint, T Data);

/// <summary>
/// Non-generic helpers for cache key handling. The symbol cache stores
/// file-path keys in a canonical form (forward slashes, mixed case preserved)
/// regardless of how they were produced (Path.Combine may leave mixed slashes
/// on Windows; Path.GetFullPath produces backslashes). Keeping this rule in
/// one place prevents callers from diverging and re-introducing lookup misses.
/// </summary>
public static class SymbolCacheKeys
{
    /// <summary>
    /// Returns the canonical cache-key form of <paramref name="filePath"/>.
    /// </summary>
    public static string Normalize(string filePath) => filePath.Replace('\\', '/');
}

/// <summary>
/// On-disk cache file format.
/// </summary>
public sealed record CacheFile<T>(int Version, Dictionary<string, CacheEntry<T>> Entries);

/// <summary>
/// Utilities for computing cache fingerprints.
/// </summary>
public static class CacheFingerprint
{
    /// <summary>
    /// Computes a fingerprint for a file based on its content hash and modification time.
    /// </summary>
    public static string ForFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return string.Empty;
        }

        var info = new FileInfo(filePath);
        byte[] data = Encoding.UTF8.GetBytes($"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();
    }

    /// <summary>
    /// Computes a fingerprint from file content bytes.
    /// </summary>
    public static string ForContent(string content) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
}

internal static class CacheJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };
}
