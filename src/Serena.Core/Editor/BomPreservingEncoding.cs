// v1.0.34: BOM-preserving encoding resolver. Default File.WriteAllTextAsync uses
// a UTF-8 encoder without BOM, which silently strips the BOM from files that
// originally had one (a problem for some Windows-authored .csproj files and
// other tools that detect encoding via BOM). This resolver inspects the
// existing file (if any) and returns a UTF8Encoding configured to preserve
// whatever BOM state the file already has.

using System.Text;

namespace Serena.Core.Editor;

public static class BomPreservingEncoding
{
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
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        byte[] head = new byte[3];
        int read;
        await using (FileStream fs = new(
            absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = await fs.ReadAsync(head.AsMemory(0, 3), ct).ConfigureAwait(false);
        }

        bool hasBom = read == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
    }
}
