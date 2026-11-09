// Per-path gate for edits. Parallel edit tools targeting the same file would
// otherwise collide on Windows file locks, and since 0.7.1 the gate covers the
// whole read, locate and write of an edit: four symbol edits issued in the same
// second on one file each read the original and overwrote one another. A tool
// takes a lease on the path before it reads and writes through the lease;
// unrelated paths never wait on each other.

using System.Collections.Concurrent;
using System.Text;

namespace Serena.Core.Editor;

public static class FileWriteGate
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _gates =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Holds the path's gate for the whole of a read-modify-write. Disposing the
    /// lease releases it. A second caller for the same path waits here, then reads
    /// the file as the first caller left it.
    /// </summary>
    public static async Task<FileLease> LockAsync(string absolutePath, CancellationToken ct = default)
    {
        string key = Path.GetFullPath(absolutePath);
        SemaphoreSlim gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        if (!await gate.WaitAsync(DefaultTimeout, ct).ConfigureAwait(false))
        {
            throw new IOException(
                $"Timed out waiting {DefaultTimeout.TotalSeconds:F0}s for write lock on '{absolutePath}'.");
        }

        return new FileLease(key, gate);
    }

    public static Task WriteAllTextAsync(
        string absolutePath, string content, CancellationToken ct = default) =>
        WriteAllTextAsync(absolutePath, content, encoding: null, ct);

    /// <summary>
    /// Writes under the path's gate, for a caller that holds no lease of its own.
    /// </summary>
    public static async Task WriteAllTextAsync(
        string absolutePath, string content, Encoding? encoding, CancellationToken ct = default)
    {
        await using var lease = await LockAsync(absolutePath, ct).ConfigureAwait(false);
        await lease.WriteAllTextAsync(content, encoding, ct).ConfigureAwait(false);
    }

    private static async Task WriteUnderLeaseAsync(
        string absolutePath, string content, Encoding? encoding, CancellationToken ct)
    {
        // Always route through BomPreservingEncoding so an existing UTF-8 BOM is
        // preserved on rewrite (and absent BOMs stay absent).
        Encoding resolved = await BomPreservingEncoding
            .ResolveAsync(absolutePath, encoding, ct)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(absolutePath, content, resolved, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Exclusive access to one path between <see cref="LockAsync"/> and disposal.
    /// </summary>
    public sealed class FileLease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;

        internal FileLease(string path, SemaphoreSlim gate)
        {
            Path = path;
            _gate = gate;
        }

        /// <summary>The full path the lease holds.</summary>
        public string Path { get; }

        public Task WriteAllTextAsync(string content, Encoding? encoding, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_gate is null, this);
            return WriteUnderLeaseAsync(Path, content, encoding, ct);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
