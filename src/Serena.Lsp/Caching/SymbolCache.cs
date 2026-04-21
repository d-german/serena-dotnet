// Symbol Caching Subsystem - Ported from solidlsp/ls.py cache methods + solidlsp/util/cache.py
// Phase 3C: Cache persistence, fingerprinting, invalidation

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

    private Dictionary<string, CacheEntry<T>> _entries = [];
    private bool _isModified;

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
        if (_entries.TryGetValue(filePath, out var entry)
            && entry.Fingerprint == currentFingerprint)
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
        _entries[filePath] = new CacheEntry<T>(fingerprint, data);
        _isModified = true;
    }

    /// <summary>
    /// Removes a cached entry.
    /// </summary>
    public void Remove(string filePath)
    {
        if (_entries.Remove(filePath))
        {
            _isModified = true;
        }
    }

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
            var wrapper = new CacheFile<T>(_cacheVersion, _entries);
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

            _entries = wrapper.Entries;
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
}

/// <summary>
/// A single cached entry with its fingerprint for staleness detection.
/// </summary>
public sealed record CacheEntry<T>(string Fingerprint, T Data);

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
