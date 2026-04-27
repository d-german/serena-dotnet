// v1.0.28: Per-path write gate. Multiple parallel edit tools targeting the same
// file would otherwise collide on Windows file locks
// (IOException: "process cannot access the file because it is being used by
// another process"). The gate serializes writes per absolute path while
// allowing unrelated paths to write concurrently.

using System.Collections.Concurrent;
using System.Text;

namespace Serena.Core.Editor;

public static class FileWriteGate
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static Task WriteAllTextAsync(
        string absolutePath, string content, CancellationToken ct = default) =>
        WriteAllTextAsync(absolutePath, content, encoding: null, ct);

    public static async Task WriteAllTextAsync(
        string absolutePath, string content, Encoding? encoding, CancellationToken ct = default)
    {
        string key = Path.GetFullPath(absolutePath);
        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(DefaultTimeout, ct).ConfigureAwait(false))
        {
            throw new IOException(
                $"Timed out waiting {DefaultTimeout.TotalSeconds:F0}s for write lock on '{absolutePath}'.");
        }

        try
        {
            // v1.0.34: Always route through BomPreservingEncoding so an existing
            // UTF-8 BOM is preserved on rewrite (and absent BOMs stay absent).
            Encoding resolved = await BomPreservingEncoding
                .ResolveAsync(absolutePath, encoding, ct)
                .ConfigureAwait(false);
            await File.WriteAllTextAsync(absolutePath, content, resolved, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
