// v1.0.34: BomPreservingEncoding resolver tests.

using System.Text;
using FluentAssertions;
using Serena.Core.Editor;

namespace Serena.Core.Tests;

public class BomPreservingEncodingTests
{
    private static byte[] BOM => new byte[] { 0xEF, 0xBB, 0xBF };

    [Fact]
    public async Task FileWithoutBom_StaysBomFree_AfterWriteThroughGate()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "no-bom.txt");
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("hello"));

            await FileWriteGate.WriteAllTextAsync(path, "world", encoding: null, CancellationToken.None);

            byte[] head = await File.ReadAllBytesAsync(path);
            head.Take(3).SequenceEqual(BOM).Should().BeFalse();
            Encoding.UTF8.GetString(head).Should().Be("world");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task FileWithBom_KeepsBom_AfterWriteThroughGate()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "with-bom.txt");
            byte[] initial = BOM.Concat(Encoding.UTF8.GetBytes("hello")).ToArray();
            await File.WriteAllBytesAsync(path, initial);

            await FileWriteGate.WriteAllTextAsync(path, "world", encoding: null, CancellationToken.None);

            byte[] head = await File.ReadAllBytesAsync(path);
            head.Take(3).SequenceEqual(BOM).Should().BeTrue();
            Encoding.UTF8.GetString(head, 3, head.Length - 3).Should().Be("world");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NewFile_WrittenWithoutBom()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "new.txt");

            Encoding resolved = await BomPreservingEncoding.ResolveAsync(path, requested: null, CancellationToken.None);

            resolved.Should().BeOfType<UTF8Encoding>();
            resolved.GetPreamble().Should().BeEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task NonUtf8Encoding_PassedThroughUnchanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "ignored.txt");
            Encoding requested = Encoding.Unicode; // UTF-16 LE

            Encoding resolved = await BomPreservingEncoding.ResolveAsync(path, requested, CancellationToken.None);

            resolved.Should().BeSameAs(requested);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
