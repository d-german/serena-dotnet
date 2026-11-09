// The occurrence side of the symbol index: which identifiers appear in which
// file, on which lines. Built from text, so it needs no language server, and
// kept in the same SQLite database as the outlines but in its own tables, so
// a file whose outline cannot be rebuilt (no server running) still has fresh
// references. The owning SymbolCache drives persistence; this class owns the
// pending state, the schema and the queries.

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Serena.Lsp.Caching;

/// <summary>
/// What the occurrence index needs from the cache that stores it: the shared
/// database, the lock that serializes writes, a way to flush pending rows
/// before a query, and a way to drop a file whose text has disappeared.
/// </summary>
internal interface IOccurrenceStore
{
    object SyncRoot { get; }
    bool DatabaseLoaded { get; }
    SqliteConnection OpenDatabase();
    void Flush();
    void MarkModified();
    void RemoveFile(string normalizedKey);
}

public sealed class OccurrenceIndex
{
    private readonly IOccurrenceStore _store;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, int[]>> _pending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _removed = new(StringComparer.OrdinalIgnoreCase);

    internal OccurrenceIndex(IOccurrenceStore store, ILogger logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Whether <see cref="Capture"/> records anything. On by default; an index
    /// built for a payload that is not source code turns it off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    internal bool HasPendingChanges => !_pending.IsEmpty || !_removed.IsEmpty;

    /// <summary>
    /// Rebuilds the file's rows from its current text. Returns false when the
    /// index is disabled or the file is missing, too large or unreadable; the
    /// existing rows are then left alone. Reads the file outside the store's
    /// lock, since indexing captures several files in parallel.
    /// </summary>
    public bool Capture(string filePath)
    {
        if (!Enabled)
        {
            return false;
        }
        var occurrences = OccurrenceIndexer.TryExtractFromFile(filePath);
        if (occurrences is null)
        {
            return false;
        }
        string key = SymbolCacheKeys.Normalize(filePath);
        lock (_store.SyncRoot)
        {
            _pending[key] = occurrences;
            _removed.TryRemove(key, out _);
        }
        _store.MarkModified();
        return true;
    }

    /// <summary>
    /// Drops the file's rows on the next save. A no-op when nothing could be
    /// stored for the file yet, so removing an unknown file never creates a
    /// database.
    /// </summary>
    public void Remove(string filePath)
    {
        string key = SymbolCacheKeys.Normalize(filePath);
        lock (_store.SyncRoot)
        {
            bool pending = _pending.TryRemove(key, out _);
            if (!pending && !_store.DatabaseLoaded)
            {
                return;
            }
            _removed[key] = 0;
        }
        _store.MarkModified();
    }

    /// <summary>
    /// Files (and the one-based lines in each) where <paramref name="name"/>
    /// occurs as an identifier. A file that no longer exists on disk is left
    /// out and removed from the index. Empty when the index has no occurrence
    /// rows at all; see <see cref="RowCount"/> to tell that apart from "no uses".
    /// </summary>
    public IReadOnlyList<OccurrenceHit> Query(string name, string? pathPrefix)
    {
        _store.Flush();
        if (!_store.DatabaseLoaded)
        {
            return [];
        }

        string normalizedPrefix = pathPrefix is null ? string.Empty : SymbolCacheKeys.Normalize(pathPrefix);
        var hits = new List<OccurrenceHit>();
        var missing = new List<string>();
        lock (_store.SyncRoot)
        {
            using var connection = _store.OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT p.path, o.lines
                FROM occurrences AS o
                JOIN occurrence_paths AS p ON p.id = o.path_id
                WHERE o.name = $name COLLATE NOCASE
                ORDER BY p.path;
                """;
            command.Parameters.AddWithValue("$name", name);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string path = reader.GetString(0);
                if (normalizedPrefix.Length > 0 && !IsWithinPath(path, normalizedPrefix))
                {
                    continue;
                }
                if (!File.Exists(path))
                {
                    missing.Add(path);
                    continue;
                }
                int[] lines = reader.GetString(1)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s, out int line) ? line : -1)
                    .Where(line => line > 0)
                    .ToArray();
                hits.Add(new OccurrenceHit(path, lines));
            }
        }

        foreach (string path in missing)
        {
            _logger.LogDebug("Dropping index rows for deleted file {Path}", path);
            _store.RemoveFile(path);
        }
        return hits;
    }

    /// <summary>Number of (file, identifier) rows; 0 before the first index with occurrence capture.</summary>
    public long RowCount()
    {
        _store.Flush();
        if (!_store.DatabaseLoaded)
        {
            return 0;
        }
        lock (_store.SyncRoot)
        {
            using var connection = _store.OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM occurrences;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The normalized paths that have occurrence rows.</summary>
    public IReadOnlyCollection<string> Paths()
    {
        _store.Flush();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_store.DatabaseLoaded)
        {
            return paths;
        }
        lock (_store.SyncRoot)
        {
            using var connection = _store.OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path FROM occurrence_paths;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                paths.Add(reader.GetString(0));
            }
        }
        return paths;
    }

    /// <summary>
    /// Captures every file in <paramref name="absolutePaths"/> that has no
    /// rows yet, so an index created before occurrence capture gains them on
    /// the next <c>project index</c> without re-outlining unchanged files.
    /// Returns the number of files captured.
    /// </summary>
    public int Backfill(IEnumerable<string> absolutePaths)
    {
        if (!Enabled)
        {
            return 0;
        }
        var covered = Paths();
        int captured = 0;
        foreach (string absolutePath in absolutePaths)
        {
            if (!covered.Contains(SymbolCacheKeys.Normalize(absolutePath)) && Capture(absolutePath))
            {
                captured++;
            }
        }
        if (captured > 0)
        {
            _store.Flush();
        }
        return captured;
    }

    // -- persistence, called by the owning cache inside its save transaction --

    internal void WritePending(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (string key in _removed.Keys)
        {
            SymbolCacheSql.ExecuteNonQuery(connection, transaction,
                "DELETE FROM occurrence_paths WHERE path = $path;", ("$path", key));
        }

        foreach (var (key, occurrences) in _pending)
        {
            long pathId = EnsurePathId(connection, transaction, key);
            SymbolCacheSql.ExecuteNonQuery(connection, transaction,
                "DELETE FROM occurrences WHERE path_id = $pathId;", ("$pathId", pathId));
            foreach (var (name, lines) in occurrences)
            {
                SymbolCacheSql.ExecuteNonQuery(connection, transaction,
                    "INSERT OR REPLACE INTO occurrences(path_id, name, lines) VALUES($pathId, $name, $lines);",
                    ("$pathId", pathId), ("$name", name), ("$lines", string.Join(',', lines)));
            }
        }

        _removed.Clear();
        _pending.Clear();
    }

    /// <summary>
    /// The occurrence tables are additive: an index built before they existed
    /// gains the (empty) tables on first open without a version bump or a
    /// rebuild, and fills them on the next index or inline refresh.
    /// </summary>
    internal static void EnsureSchema(SqliteConnection connection)
    {
        // Rows reference a small path table of their own, not the outline
        // entries: a file edited with no language server running loses its
        // outline but keeps fresh references, and the path is stored once per
        // file instead of once per identifier.
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS occurrence_paths(
                    id INTEGER PRIMARY KEY,
                    path TEXT NOT NULL UNIQUE COLLATE NOCASE
                );
                """;
            create.ExecuteNonQuery();
        }

        // Two pre-release 0.6.0 shapes are converted in place: keyed by outline
        // entry id, and keyed by the full path text on every row.
        string? migration = null;
        if (SymbolCacheSql.TableHasColumn(connection, "occurrences", "entry_id"))
        {
            migration = """
                INSERT OR IGNORE INTO occurrence_paths(path)
                    SELECT DISTINCT e.path FROM occurrences_v1 AS o JOIN entries AS e ON e.id = o.entry_id;
                INSERT OR IGNORE INTO occurrences(path_id, name, lines)
                    SELECT p.id, o.name, o.lines
                    FROM occurrences_v1 AS o
                    JOIN entries AS e ON e.id = o.entry_id
                    JOIN occurrence_paths AS p ON p.path = e.path COLLATE NOCASE;
                """;
        }
        else if (SymbolCacheSql.TableHasColumn(connection, "occurrences", "path"))
        {
            migration = """
                INSERT OR IGNORE INTO occurrence_paths(path)
                    SELECT DISTINCT path FROM occurrences_v1;
                INSERT OR IGNORE INTO occurrences(path_id, name, lines)
                    SELECT p.id, o.name, o.lines
                    FROM occurrences_v1 AS o
                    JOIN occurrence_paths AS p ON p.path = o.path COLLATE NOCASE;
                """;
        }

        if (migration is not null)
        {
            using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                DROP INDEX IF EXISTS ix_occurrences_name;
                ALTER TABLE occurrences RENAME TO occurrences_v1;
                CREATE TABLE occurrences(
                    path_id INTEGER NOT NULL REFERENCES occurrence_paths(id) ON DELETE CASCADE,
                    name TEXT NOT NULL COLLATE NOCASE,
                    lines TEXT NOT NULL,
                    PRIMARY KEY(path_id, name)
                );
                """ + migration + """
                DROP TABLE occurrences_v1;
                """;
            migrate.ExecuteNonQuery();

            // Reclaim the dropped table's pages; on a large index they double the file.
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS occurrences(
                path_id INTEGER NOT NULL REFERENCES occurrence_paths(id) ON DELETE CASCADE,
                name TEXT NOT NULL COLLATE NOCASE,
                lines TEXT NOT NULL,
                PRIMARY KEY(path_id, name)
            );
            CREATE INDEX IF NOT EXISTS ix_occurrences_name ON occurrences(name COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }

    internal const string ClearAllSql = """
        DELETE FROM occurrences;
        DELETE FROM occurrence_paths;
        """;

    private static long EnsurePathId(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        SymbolCacheSql.ExecuteNonQuery(connection, transaction,
            "INSERT OR IGNORE INTO occurrence_paths(path) VALUES($path);", ("$path", path));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM occurrence_paths WHERE path = $path COLLATE NOCASE;";
        command.Parameters.AddWithValue("$path", path);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static bool IsWithinPath(string candidate, string scope)
    {
        if (candidate.Equals(scope, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        string prefix = scope.EndsWith('/') ? scope : scope + "/";
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// One file in which an identifier occurs, with the one-based lines.
/// </summary>
public sealed record OccurrenceHit(string Path, int[] Lines);

/// <summary>Small SQLite helpers shared by the cache and its occurrence index.</summary>
internal static class SymbolCacheSql
{
    public static void ExecuteNonQuery(
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

    public static bool TableHasColumn(SqliteConnection connection, string tableName, string columnName)
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
}
