// Symbol Caching Subsystem - Ported from solidlsp/ls.py cache methods + solidlsp/util/cache.py
// Phase 3C: Cache persistence, fingerprinting, invalidation

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Serena.Lsp.Client;

namespace Serena.Lsp.Caching;

/// <summary>
/// Versioned, fingerprinted cache for document symbols. New caches use a
/// SQLite entry store plus a compact leaf-name index so a large-repository
/// symbol query does not deserialize the entire cache. Legacy JSON caches are
/// migrated on first load.
/// </summary>
public sealed class SymbolCache<T>
{
    private const string InvalidationsFilename = "invalidated-paths.txt";
    private const int DatabaseSchemaVersion = 2;

    private readonly string _cacheDir;
    private readonly string _cacheFilename;
    private readonly string _databaseFilename;
    private readonly int _cacheVersion;
    private readonly ILogger _logger;
    private readonly object _persistenceLock = new();

    private ConcurrentDictionary<string, CacheEntry<T>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirtyKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _removedKeys = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _databaseLoaded;
    private int _persistedCount;
    private volatile bool _isModified;

    public SymbolCache(string cacheDir, string cacheFilename, int cacheVersion, ILogger logger)
    {
        _cacheDir = cacheDir;
        _cacheFilename = cacheFilename;
        _databaseFilename = Path.ChangeExtension(cacheFilename, ".db");
        _cacheVersion = cacheVersion;
        _logger = logger;
    }

    /// <summary>
    /// Tries to get a cached value for the given file path.
    /// Returns null if the cache entry is missing or stale.
    /// </summary>
    public T? TryGet(string filePath, string currentFingerprint)
    {
        if (TryGetEntry(NormalizeKey(filePath), out var entry)
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
        if (TryGetEntry(NormalizeKey(filePath), out var entry))
        {
            return entry.Data;
        }
        return default;
    }

    /// <summary>
    /// Returns the normalized paths whose persisted fingerprints match the
    /// supplied candidates. This performs one sequential database read rather
    /// than opening SQLite once per file, which keeps no-change indexing fast
    /// for large solutions.
    /// </summary>
    public IReadOnlySet<string> GetFreshPaths(
        IEnumerable<(string FilePath, string Fingerprint)> candidates)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (filePath, fingerprint) in candidates)
        {
            if (fingerprint.Length > 0)
            {
                expected[NormalizeKey(filePath)] = fingerprint;
            }
        }

        var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, entry) in _entries)
        {
            if (!_removedKeys.ContainsKey(path)
                && expected.TryGetValue(path, out string? fingerprint)
                && entry.Fingerprint == fingerprint)
            {
                fresh.Add(path);
            }
        }

        if (!_databaseLoaded || expected.Count == 0)
        {
            return fresh;
        }

        lock (_persistenceLock)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, fingerprint FROM entries;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                if (!_removedKeys.ContainsKey(path)
                    && expected.TryGetValue(path, out string? fingerprint)
                    && reader.GetString(1) == fingerprint)
                {
                    fresh.Add(path);
                }
            }
        }
        return fresh;
    }

    /// <summary>
    /// Stores a value in the cache.
    /// </summary>
    public void Set(string filePath, string fingerprint, T data)
    {
        lock (_persistenceLock)
        {
            string key = NormalizeKey(filePath);
            _entries[key] = new CacheEntry<T>(fingerprint, data);
            _dirtyKeys[key] = 0;
            _removedKeys.TryRemove(key, out _);
            _isModified = true;
        }
    }

    /// <summary>
    /// Removes a cached entry.
    /// </summary>
    public void Remove(string filePath)
    {
        lock (_persistenceLock)
        {
            string key = NormalizeKey(filePath);
            if (_entries.TryRemove(key, out _) || ContainsPersistedKey(key))
            {
                _dirtyKeys.TryRemove(key, out _);
                _removedKeys[key] = 0;
                _isModified = true;
                if (File.Exists(CachePath) || File.Exists(DatabasePath))
                {
                    RecordInvalidation(key);
                }
            }
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
            lock (_persistenceLock)
            {
                Directory.CreateDirectory(_cacheDir);
                using var connection = OpenDatabase();
                EnsureDatabaseSchema(connection);
                ResetDatabaseIfVersionChanged(connection);

                using var transaction = connection.BeginTransaction();
                foreach (string key in _removedKeys.Keys)
                {
                    ExecuteNonQuery(connection, transaction,
                        """
                        DELETE FROM symbol_names
                        WHERE entry_id = (SELECT id FROM entries WHERE path = $path);
                        DELETE FROM entries WHERE path = $path;
                        """,
                        ("$path", key));
                }

                foreach (string key in _dirtyKeys.Keys)
                {
                    if (!_entries.TryGetValue(key, out var entry))
                    {
                        continue;
                    }

                    string dataJson = JsonSerializer.Serialize(entry.Data, CacheJsonOptions.Default);
                    ExecuteNonQuery(connection, transaction,
                        """
                        INSERT INTO entries(path, fingerprint, data)
                        VALUES($path, $fingerprint, $data)
                        ON CONFLICT(path) DO UPDATE SET
                            fingerprint = excluded.fingerprint,
                            data = excluded.data;
                        DELETE FROM symbol_names
                        WHERE entry_id = (SELECT id FROM entries WHERE path = $path);
                        """,
                        ("$path", key), ("$fingerprint", entry.Fingerprint), ("$data", dataJson));

                    long entryId = QueryEntryId(connection, transaction, key);

                    if (entry.Data is UnifiedSymbolInformation[] symbols)
                    {
                        foreach (string leafName in EnumerateLeafNames(symbols)
                                     .Distinct(StringComparer.OrdinalIgnoreCase))
                        {
                            ExecuteNonQuery(connection, transaction,
                                "INSERT OR IGNORE INTO symbol_names(entry_id, leaf_name) VALUES($entryId, $leaf);",
                                ("$entryId", entryId), ("$leaf", leafName));
                        }
                    }
                }

                ExecuteNonQuery(connection, transaction,
                    "INSERT OR REPLACE INTO metadata(key, value) VALUES('cache_version', $version);",
                    ("$version", _cacheVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                transaction.Commit();

                _databaseLoaded = true;
                _persistedCount = QueryCount(connection);
                _dirtyKeys.Clear();
                _removedKeys.Clear();
                _entries.Clear();
                TryDeleteInvalidationsFile();
                _isModified = false;

                // The database is now authoritative. Removing the legacy JSON
                // avoids keeping two 398 MB copies for very large repositories.
                TryDeleteLegacyCacheFile();
            }
            _logger.LogDebug("Saved cache {File} ({Count} entries)", _databaseFilename, _persistedCount);
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
        if (File.Exists(DatabasePath))
        {
            return LoadDatabase();
        }

        if (!File.Exists(CachePath))
        {
            return false;
        }

        try
        {
            string json = File.ReadAllText(CachePath, Encoding.UTF8);
            var wrapper = JsonSerializer.Deserialize<CacheFile<T>>(json, CacheJsonOptions.Default);
            if (wrapper is null || wrapper.Version != _cacheVersion)
            {
                _logger.LogInformation("Cache version mismatch for {File}, rebuilding", _cacheFilename);
                return false;
            }

            var normalizedEntries = new ConcurrentDictionary<string, CacheEntry<T>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (path, entry) in wrapper.Entries)
            {
                normalizedEntries[NormalizeKey(path)] = entry;
            }
            _entries = normalizedEntries;
            foreach (string key in normalizedEntries.Keys)
            {
                _dirtyKeys[key] = 0;
            }
            ApplyRecordedInvalidationsToMemory();
            _isModified = true;
            _logger.LogInformation(
                "Migrating legacy cache {File} ({Count} entries) to indexed SQLite store",
                _cacheFilename, _entries.Count);
            Save();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load cache {File}", _cacheFilename);
            return false;
        }
    }

    public int Count
    {
        get
        {
            if (!_databaseLoaded)
            {
                return _entries.Count;
            }
            if (!_isModified)
            {
                return _persistedCount;
            }
            return Keys.Count;
        }
    }

    /// <summary>
    /// Returns a snapshot of all cached file paths.
    /// </summary>
    public IReadOnlyCollection<string> Keys
    {
        get
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_databaseLoaded)
            {
                lock (_persistenceLock)
                {
                    using var connection = OpenDatabase();
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT path FROM entries;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        keys.Add(reader.GetString(0));
                    }
                }
            }
            keys.UnionWith(_entries.Keys);
            keys.ExceptWith(_removedKeys.Keys);
            return keys.ToList();
        }
    }

    /// <summary>
    /// Uses the persistent symbol-name index to return only files that can
    /// contain a matching declaration. Returns false for non-symbol cache
    /// payloads or before a database has been created, allowing callers to
    /// fall back to the legacy key scan.
    /// </summary>
    public bool TryGetCandidatePaths(
        string leafName,
        bool substringMatching,
        string? pathPrefix,
        out IReadOnlyList<string> paths)
    {
        paths = [];
        if (typeof(T) != typeof(UnifiedSymbolInformation[]))
        {
            return false;
        }
        if (_isModified)
        {
            Save();
        }
        if (!_databaseLoaded)
        {
            return false;
        }

        string normalizedPrefix = pathPrefix is null ? string.Empty : NormalizeKey(pathPrefix);
        lock (_persistenceLock)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = substringMatching
                ? """
                  SELECT DISTINCT e.path
                  FROM symbol_names AS s
                  JOIN entries AS e ON e.id = s.entry_id
                  WHERE instr(lower(s.leaf_name), lower($leaf)) > 0;
                  """
                : """
                  SELECT DISTINCT e.path
                  FROM symbol_names AS s
                  JOIN entries AS e ON e.id = s.entry_id
                  WHERE s.leaf_name = $leaf COLLATE NOCASE;
                  """;
            command.Parameters.AddWithValue("$leaf", StripOverloadIndex(leafName));
            using var reader = command.ExecuteReader();
            var candidates = new List<string>();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                if (normalizedPrefix.Length == 0 || IsWithinPath(path, normalizedPrefix))
                {
                    candidates.Add(path);
                }
            }
            paths = candidates;
            return true;
        }
    }

    private string CachePath => Path.Combine(_cacheDir, _cacheFilename);

    private string DatabasePath => Path.Combine(_cacheDir, _databaseFilename);

    private string InvalidationsPath => Path.Combine(_cacheDir, InvalidationsFilename);

    private void RecordInvalidation(string normalizedKey)
    {
        try
        {
            lock (_persistenceLock)
            {
                Directory.CreateDirectory(_cacheDir);
                File.AppendAllText(InvalidationsPath, normalizedKey + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist cache invalidation for {Path}", normalizedKey);
        }
    }

    private void ApplyRecordedInvalidationsToMemory()
    {
        string invalidationsPath = InvalidationsPath;
        if (!File.Exists(invalidationsPath))
        {
            return;
        }

        try
        {
            foreach (string line in File.ReadLines(invalidationsPath, Encoding.UTF8))
            {
                string key = NormalizeKey(line.Trim());
                if (key.Length == 0)
                {
                    continue;
                }
                if (_entries.TryRemove(key, out _))
                {
                    _dirtyKeys.TryRemove(key, out _);
                    _removedKeys[key] = 0;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to apply cache invalidation journal {Path}", invalidationsPath);
        }
    }

    private bool LoadDatabase()
    {
        try
        {
            lock (_persistenceLock)
            {
                using var connection = OpenDatabase();
                EnsureDatabaseSchema(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM metadata WHERE key = 'cache_version';";
                string? rawVersion = command.ExecuteScalar()?.ToString();
                if (!int.TryParse(rawVersion, out int version) || version != _cacheVersion)
                {
                    _logger.LogInformation("Cache version mismatch for {File}, rebuilding", _databaseFilename);
                    return false;
                }

                _databaseLoaded = true;
                _persistedCount = QueryCount(connection);
            }

            if (File.Exists(InvalidationsPath))
            {
                foreach (string line in File.ReadLines(InvalidationsPath, Encoding.UTF8))
                {
                    string key = NormalizeKey(line.Trim());
                    if (key.Length > 0)
                    {
                        _removedKeys[key] = 0;
                    }
                }
                _isModified = _removedKeys.Count > 0;
                if (_isModified)
                {
                    Save();
                }
            }

            _logger.LogDebug("Loaded indexed cache {File} ({Count} entries)", _databaseFilename, _persistedCount);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load indexed cache {File}", _databaseFilename);
            return false;
        }
    }

    private bool TryGetEntry(string key, out CacheEntry<T> entry)
    {
        if (_removedKeys.ContainsKey(key))
        {
            entry = default!;
            return false;
        }
        if (_entries.TryGetValue(key, out entry!))
        {
            return true;
        }
        if (!_databaseLoaded)
        {
            entry = default!;
            return false;
        }

        lock (_persistenceLock)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT fingerprint, data FROM entries WHERE path = $path;";
            command.Parameters.AddWithValue("$path", key);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                entry = default!;
                return false;
            }
            T? data = JsonSerializer.Deserialize<T>(reader.GetString(1), CacheJsonOptions.Default);
            if (data is null)
            {
                entry = default!;
                return false;
            }
            entry = new CacheEntry<T>(reader.GetString(0), data);
            return true;
        }
    }

    private bool ContainsPersistedKey(string key)
    {
        if (!_databaseLoaded || _removedKeys.ContainsKey(key))
        {
            return false;
        }
        lock (_persistenceLock)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM entries WHERE path = $path LIMIT 1;";
            command.Parameters.AddWithValue("$path", key);
            return command.ExecuteScalar() is not null;
        }
    }

    private SqliteConnection OpenDatabase()
    {
        // Short-lived calls intentionally open their own connections. Disable
        // pooling so cache files can be moved/deleted immediately on Windows
        // (notably during re-indexing and test cleanup).
        var connection = new SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureDatabaseSchema(SqliteConnection connection)
    {
        using (var preamble = connection.CreateCommand())
        {
            preamble.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS metadata(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            preamble.ExecuteNonQuery();
        }

        int schemaVersion = ReadSchemaVersion(connection);
        if (schemaVersion == DatabaseSchemaVersion)
        {
            return;
        }

        bool hasLegacyPathIndex = TableHasColumn(connection, "symbol_names", "path");
        if (hasLegacyPathIndex)
        {
            MigratePathIndexToNormalizedSchema(connection);
            return;
        }

        if (schemaVersion != 0)
        {
            throw new InvalidOperationException(
                $"Unsupported symbol-cache database schema {schemaVersion}; expected {DatabaseSchemaVersion}.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS entries(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                fingerprint TEXT NOT NULL,
                data TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS symbol_names(
                entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                leaf_name TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY(entry_id, leaf_name)
            );
            CREATE INDEX IF NOT EXISTS ix_symbol_names_leaf
                ON symbol_names(leaf_name COLLATE NOCASE);
            INSERT OR REPLACE INTO metadata(key, value)
                VALUES('schema_version', '2');
            """;
        command.ExecuteNonQuery();
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version';";
        string? raw = command.ExecuteScalar()?.ToString();
        return int.TryParse(raw, out int version) ? version : 0;
    }

    private static bool TableHasColumn(
        SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetString(1).Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void MigratePathIndexToNormalizedSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            ExecuteNonQuery(connection, transaction,
                """
                DROP INDEX IF EXISTS ix_symbol_names_leaf;
                ALTER TABLE entries RENAME TO entries_v1;
                ALTER TABLE symbol_names RENAME TO symbol_names_v1;
                CREATE TABLE entries(
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    fingerprint TEXT NOT NULL,
                    data TEXT NOT NULL
                );
                CREATE TABLE symbol_names(
                    entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                    leaf_name TEXT NOT NULL COLLATE NOCASE,
                    PRIMARY KEY(entry_id, leaf_name)
                );
                INSERT INTO entries(path, fingerprint, data)
                    SELECT path, fingerprint, data FROM entries_v1;
                INSERT OR IGNORE INTO symbol_names(entry_id, leaf_name)
                    SELECT e.id, s.leaf_name
                    FROM symbol_names_v1 AS s
                    JOIN entries AS e ON e.path = s.path COLLATE NOCASE;
                DROP TABLE symbol_names_v1;
                DROP TABLE entries_v1;
                CREATE INDEX ix_symbol_names_leaf
                    ON symbol_names(leaf_name COLLATE NOCASE);
                INSERT OR REPLACE INTO metadata(key, value)
                    VALUES('schema_version', '2');
                """);
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // Reclaim the repeated-path pages immediately; otherwise a migrated
        // large-repository database would keep its pre-normalization file size.
        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }

    private void ResetDatabaseIfVersionChanged(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'cache_version';";
        string? raw = command.ExecuteScalar()?.ToString();
        if (raw is null || (int.TryParse(raw, out int version) && version == _cacheVersion))
        {
            return;
        }

        using var reset = connection.CreateCommand();
        reset.CommandText = """
            DELETE FROM symbol_names;
            DELETE FROM entries;
            DELETE FROM metadata WHERE key = 'cache_version';
            """;
        reset.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static int QueryCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long QueryEntryId(
        SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM entries WHERE path = $path;";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> EnumerateLeafNames(IEnumerable<UnifiedSymbolInformation> symbols)
    {
        foreach (var symbol in symbols)
        {
            yield return StripOverloadIndex(symbol.Name);
            foreach (string child in EnumerateLeafNames(symbol.Children))
            {
                yield return child;
            }
        }
    }

    private static string StripOverloadIndex(string value)
    {
        int bracket = value.IndexOf('[');
        return bracket < 0 ? value : value[..bracket];
    }

    private static bool IsWithinPath(string candidate, string scope)
    {
        if (candidate.Equals(scope, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string prefix = scope.EndsWith('/') ? scope : scope + "/";
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void TryDeleteInvalidationsFile()
    {
        try
        {
            string invalidationsPath = InvalidationsPath;
            if (File.Exists(invalidationsPath))
            {
                File.Delete(invalidationsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete cache invalidation journal {Path}", InvalidationsPath);
        }
    }

    private void TryDeleteLegacyCacheFile()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
            }
        }
        catch (Exception ex)
        {
            // The database transaction already committed. A locked legacy
            // file should not make callers believe the indexed save failed.
            _logger.LogDebug(ex, "Failed to delete migrated legacy cache {Path}", CachePath);
        }
    }
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
