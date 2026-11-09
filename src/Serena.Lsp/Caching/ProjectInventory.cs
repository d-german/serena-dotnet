// Non-code facts the symbol index cannot see, captured at index time: package
// and project references from .csproj and .props files, and the flattened keys
// of JSON configuration files. Stored in its own SQLite file next to the
// language caches and queried by the `symbols config` and `symbols packages`
// commands, so configuration and dependency questions stop ending in grep.

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Serena.Lsp.Caching;

/// <summary>
/// One inventory row. <see cref="Kind"/> is <c>package</c>, <c>project</c>,
/// <c>property</c> (MSBuild) or <c>json</c>. <see cref="Key"/> is the package
/// id, the referenced project name, the property name, or the colon-separated
/// configuration path. <see cref="Value"/> is the version, the reference path,
/// the property value or the scalar, truncated and redacted where appropriate.
/// </summary>
public sealed record InventoryEntry(string Path, string Kind, string Key, string? Value);

public sealed class ProjectInventory
{
    public const string FileName = "inventory.db";
    private readonly string _cacheDir;
    private readonly ILogger _logger;

    public ProjectInventory(string cacheDir, ILogger logger)
    {
        _cacheDir = cacheDir;
        _logger = logger;
    }

    public string DatabasePath => Path.Combine(_cacheDir, FileName);

    public bool Exists => File.Exists(DatabasePath);

    /// <summary>
    /// Replaces the whole inventory in one transaction.
    /// </summary>
    public void Replace(IEnumerable<InventoryEntry> entries)
    {
        Directory.CreateDirectory(_cacheDir);
        using var connection = Open();
        EnsureSchema(connection);
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM inventory;";
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR REPLACE INTO inventory(path, kind, key, value) VALUES($path, $kind, $key, $value);";
        var pPath = insert.Parameters.Add("$path", SqliteType.Text);
        var pKind = insert.Parameters.Add("$kind", SqliteType.Text);
        var pKey = insert.Parameters.Add("$key", SqliteType.Text);
        var pValue = insert.Parameters.Add("$value", SqliteType.Text);
        int count = 0;
        foreach (var entry in entries)
        {
            pPath.Value = SymbolCacheKeys.Normalize(entry.Path);
            pKind.Value = entry.Kind;
            pKey.Value = entry.Key;
            pValue.Value = (object?)entry.Value ?? DBNull.Value;
            insert.ExecuteNonQuery();
            count++;
        }
        transaction.Commit();
        _logger.LogDebug("Saved inventory {File} ({Count} rows)", FileName, count);
    }

    /// <summary>
    /// Rows whose key equals or contains <paramref name="term"/>, optionally
    /// limited to some kinds and to files under a path prefix.
    /// </summary>
    public IReadOnlyList<InventoryEntry> Query(
        string term, bool substring, IReadOnlyCollection<string>? kinds, string? pathPrefix, int max)
    {
        if (!Exists)
        {
            return [];
        }

        using var connection = Open();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        string kindFilter = kinds is { Count: > 0 }
            ? " AND kind IN (" + string.Join(",", kinds.Select((_, i) => "$k" + i)) + ")"
            : string.Empty;
        command.CommandText =
            (substring
                ? "SELECT path, kind, key, value FROM inventory WHERE instr(lower(key), lower($term)) > 0"
                : "SELECT path, kind, key, value FROM inventory WHERE key = $term COLLATE NOCASE")
            + kindFilter + " ORDER BY kind, key, path;";
        command.Parameters.AddWithValue("$term", term);
        if (kinds is { Count: > 0 })
        {
            int i = 0;
            foreach (string kind in kinds)
            {
                command.Parameters.AddWithValue("$k" + i++, kind);
            }
        }

        string? normalizedPrefix = pathPrefix is null ? null : SymbolCacheKeys.Normalize(pathPrefix);
        var rows = new List<InventoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            if (normalizedPrefix is not null && !path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            rows.Add(new InventoryEntry(path, reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            if (max > 0 && rows.Count >= max)
            {
                break;
            }
        }
        return rows;
    }

    /// <summary>Every row, optionally of one kind, for the generated project overview.</summary>
    public IReadOnlyList<InventoryEntry> All(string? kind = null)
    {
        if (!Exists)
        {
            return [];
        }
        using var connection = Open();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = kind is null
            ? "SELECT path, kind, key, value FROM inventory ORDER BY kind, path, key;"
            : "SELECT path, kind, key, value FROM inventory WHERE kind = $kind ORDER BY path, key;";
        if (kind is not null)
        {
            command.Parameters.AddWithValue("$kind", kind);
        }
        var rows = new List<InventoryEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new InventoryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    /// <summary>Row count per kind, for <c>symbols stats</c>.</summary>
    public IReadOnlyDictionary<string, int> Stats()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Exists)
        {
            return result;
        }
        using var connection = Open();
        EnsureSchema(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, COUNT(*) FROM inventory GROUP BY kind;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result[reader.GetString(0)] = reader.GetInt32(1);
        }
        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            $"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS inventory(
                path TEXT NOT NULL COLLATE NOCASE,
                kind TEXT NOT NULL,
                key TEXT NOT NULL COLLATE NOCASE,
                value TEXT,
                PRIMARY KEY(path, kind, key)
            );
            CREATE INDEX IF NOT EXISTS ix_inventory_key ON inventory(key COLLATE NOCASE);
            """;
        command.ExecuteNonQuery();
    }
}
