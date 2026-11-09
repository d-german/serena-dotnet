// New files mirror the BOM state of their nearest sibling with the same extension.

using System.Text;
using FluentAssertions;
using Serena.Core.Editor;

namespace Serena.Core.Tests;

public sealed class BomMirroringTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private readonly string _tempDir;

    public BomMirroringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_bom_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    private string WriteBytes(string relative, bool bom, string text = "class X { }")
    {
        string path = Path.Combine(_tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, bom ? [.. Bom, .. body] : body);
        return path;
    }

    private static bool StartsWithBom(string path)
    {
        var head = new byte[3];
        using var fs = File.OpenRead(path);
        return fs.Read(head, 0, 3) == 3 && head.AsSpan().SequenceEqual(Bom);
    }

    [Fact]
    public async Task NewFile_NextToABomSibling_GetsABom()
    {
        WriteBytes("Data/Existing.cs", bom: true);
        string newPath = Path.Combine(_tempDir, "Data", "New.cs");

        await FileWriteGate.WriteAllTextAsync(newPath, "class New { }", Encoding.UTF8);

        StartsWithBom(newPath).Should().BeTrue();
    }

    [Fact]
    public async Task NewFile_NextToABomFreeSibling_StaysBomFree()
    {
        WriteBytes("Data/Existing.cs", bom: false);
        string newPath = Path.Combine(_tempDir, "Data", "New.cs");

        await FileWriteGate.WriteAllTextAsync(newPath, "class New { }", Encoding.UTF8);

        StartsWithBom(newPath).Should().BeFalse();
    }

    [Fact]
    public async Task NewFile_InAnEmptyDirectory_LooksAtParents_AndOnlyAtTheSameExtension()
    {
        WriteBytes("Root.cs", bom: true);
        WriteBytes("Data/notes.txt", bom: false);
        string newPath = Path.Combine(_tempDir, "Data", "Fresh", "New.cs");

        (await BomPreservingEncoding.SiblingHasBomAsync(newPath, CancellationToken.None)).Should().BeTrue();
        (await BomPreservingEncoding.SiblingHasBomAsync(Path.Combine(_tempDir, "Data", "other.txt"), CancellationToken.None))
            .Should().BeFalse("the .txt sibling has no BOM and .cs files are not consulted for a .txt file");
    }

    [Fact]
    public async Task NewFile_WithNoSiblingAnywhere_IsBomFree()
    {
        string newPath = Path.Combine(_tempDir, "Alone", "New.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);

        await FileWriteGate.WriteAllTextAsync(newPath, "class New { }", Encoding.UTF8);

        StartsWithBom(newPath).Should().BeFalse();
    }

    [Fact]
    public async Task ExistingFile_KeepsItsOwnBomState_RegardlessOfSiblings()
    {
        WriteBytes("Data/Bom.cs", bom: true);
        string plain = WriteBytes("Data/Plain.cs", bom: false);

        await FileWriteGate.WriteAllTextAsync(plain, "class Plain { }", Encoding.UTF8);

        StartsWithBom(plain).Should().BeFalse();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
