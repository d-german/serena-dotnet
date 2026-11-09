// v1.0.34: BOM-preserving encoding resolver. Default File.WriteAllTextAsync uses
// a UTF-8 encoder without BOM, which silently strips the BOM from files that
// originally had one (a problem for some Windows-authored .csproj files and
// other tools that detect encoding via BOM). This resolver inspects the
// existing file (if any) and returns a UTF8Encoding configured to preserve
// whatever BOM state the file already has.
//
// 0.5.0: a file that does not exist yet mirrors the BOM state of its nearest
// sibling with the same extension (same directory first, then up to three
// parent directories), so a new .cs file in a BOM-everywhere repository gets
// a BOM and one in a BOM-free repository does not.

using System.Text;

namespace Serena.Core.Editor;

public static class BomPreservingEncoding
{
    private const int MaxParentLevels = 3;

    public static async Task<Encoding> ResolveAsync(
        string absolutePath, Encoding? requested, CancellationToken ct)
    {
        // Non-UTF-8 (or non-default UTF-8) requests pass through unchanged.
        if (requested is not null && requested is not UTF8Encoding)
        {
            return requested;
        }

        if (!File.Exists(absolutePath))
        {
            bool mirrored = await SiblingHasBomAsync(absolutePath, ct).ConfigureAwait(false);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: mirrored);
        }

        bool hasBom = await HasBomAsync(absolutePath, ct).ConfigureAwait(false);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
    }

    /// <summary>
    /// Whether the nearest existing file with the same extension carries a BOM.
    /// Looks in the new file's directory, then in up to three parents. False
    /// when no such file exists.
    /// </summary>
    public static async Task<bool> SiblingHasBomAsync(string absolutePath, CancellationToken ct)
    {
        string extension = Path.GetExtension(absolutePath);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(absolutePath);
        for (int level = 0; dir is not null && level <= MaxParentLevels; level++)
        {
            if (Directory.Exists(dir))
            {
                string? sibling = Directory.EnumerateFiles(dir, "*" + extension, SearchOption.TopDirectoryOnly)
                    .Where(f => !string.Equals(f, absolutePath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (sibling is not null)
                {
                    return await HasBomAsync(sibling, ct).ConfigureAwait(false);
                }
            }

            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    private static async Task<bool> HasBomAsync(string path, CancellationToken ct)
    {
        byte[] head = new byte[3];
        int read;
        await using (FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = await fs.ReadAsync(head.AsMemory(0, 3), ct).ConfigureAwait(false);
        }

        return read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }
}
