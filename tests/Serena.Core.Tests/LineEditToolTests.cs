// Comprehensive tests for line edit tools: DeleteLinesTool, InsertAtLineTool, ReplaceLinesTool.
// Tests cover ParseFileContent edge cases, phantom line bugs, roundtrip correctness,
// and the exact failure modes encountered during the code review session.

using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Tools;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

/// <summary>
/// Base class providing shared setup for line edit tool tests.
/// Each test gets a fresh temp directory and tool instances.
/// </summary>
public abstract class LineEditTestBase : IDisposable
{
    protected readonly string TempDir;
    protected readonly ToolRegistry Registry;
    protected readonly ITool DeleteTool;
    protected readonly ITool InsertTool;
    protected readonly ITool ReplaceTool;

    protected LineEditTestBase()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "serena_le_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(TempDir);

        var loggerFactory = NullLoggerFactory.Instance;
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var lsRegistry = new LanguageServerRegistry();
        var agent = new SerenaAgent(config, lsRegistry, loggerFactory);
        var context = new AgentToolContext(agent, loggerFactory);

        Registry = new ToolRegistry();
        Registry.Register(new DeleteLinesTool(context));
        Registry.Register(new InsertAtLineTool(context));
        Registry.Register(new ReplaceLinesTool(context));
        agent.SetToolRegistry(Registry);
        agent.ActivateProjectAsync(TempDir).Wait();

        DeleteTool = Registry.Get("delete_lines")!;
        InsertTool = Registry.Get("insert_at_line")!;
        ReplaceTool = Registry.Get("replace_lines")!;
    }

    protected string CreateFile(string name, string content)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllText(path, content);
        return name;
    }

    protected string ReadFile(string name)
    {
        return File.ReadAllText(Path.Combine(TempDir, name));
    }

    protected string[] ReadLines(string name)
    {
        string content = ReadFile(name);
        // Split same way as ParseFileContent for comparison
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var lines = content.Split('\n');
        if (lines.Length > 0 && lines[^1] == "")
        {
            return lines[..^1].Select(l => l.TrimEnd('\r')).ToArray();
        }

        return lines.Select(l => l.TrimEnd('\r')).ToArray();
    }

    public void Dispose()
    {
        try { Directory.Delete(TempDir, true); } catch { }
    }
}

// ═══════════════════════════════════════════════════════════════════
// ParseFileContent Tests (via observable behavior through tools)
// Since ParseFileContent is file-static, we test it indirectly.
// ═══════════════════════════════════════════════════════════════════

public class ParseFileContentTests : LineEditTestBase
{
    // We test ParseFileContent indirectly by writing files with specific content
    // and verifying that delete/insert/replace operations produce correct results.
    // If ParseFileContent has bugs, these tests will reveal them.

    [Fact]
    public async Task EmptyFile_DeleteReturnsNoLinesDeleted()
    {
        string file = CreateFile("empty.txt", "");
        var result = await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 1,
            ["end_line"] = 1,
        });
        result.Should().Contain("No lines deleted");
    }

    [Fact]
    public async Task SingleLineNoNewline_ReplacePreservesFormat()
    {
        string file = CreateFile("single.txt", "hello");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 1,
            ["end_line"] = 1,
            ["new_content"] = "world",
        });
        ReadFile(file).Should().Be("world");
    }

    [Fact]
    public async Task SingleLineWithNewline_ReplacePreservesTrailingNewline()
    {
        string file = CreateFile("single_nl.txt", "hello\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 1,
            ["end_line"] = 1,
            ["new_content"] = "world",
        });
        ReadFile(file).Should().Be("world\n");
    }

    [Fact]
    public async Task CrlfFile_PreservesLineEndings()
    {
        string file = CreateFile("crlf.txt", "line1\r\nline2\r\nline3\r\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 2,
            ["end_line"] = 2,
            ["new_content"] = "replaced",
        });
        string content = ReadFile(file);
        content.Should().Be("line1\r\nreplaced\r\nline3\r\n");
    }

    [Fact]
    public async Task LfFile_PreservesLineEndings()
    {
        string file = CreateFile("lf.txt", "line1\nline2\nline3\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 2,
            ["end_line"] = 2,
            ["new_content"] = "replaced",
        });
        string content = ReadFile(file);
        content.Should().Be("line1\nreplaced\nline3\n");
    }

    [Fact]
    public async Task FileWithOnlyNewlines_HandledCorrectly()
    {
        string file = CreateFile("newlines.txt", "\n\n\n");
        // This is 3 empty lines followed by a trailing newline
        var result = await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file,
            ["start_line"] = 1,
            ["end_line"] = 1,
        });
        result.Should().Contain("Deleted 1");
    }
}

// ═══════════════════════════════════════════════════════════════════
// DeleteLinesTool Tests
// ═══════════════════════════════════════════════════════════════════

public class DeleteLinesToolTests : LineEditTestBase
{
    [Fact]
    public async Task Delete_FirstLine()
    {
        string file = CreateFile("d1.txt", "aaa\nbbb\nccc\n");
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 1,
        });
        ReadFile(file).Should().Be("bbb\nccc\n");
    }

    [Fact]
    public async Task Delete_LastLine()
    {
        string file = CreateFile("d2.txt", "aaa\nbbb\nccc\n");
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 3, ["end_line"] = 3,
        });
        ReadFile(file).Should().Be("aaa\nbbb\n");
    }

    [Fact]
    public async Task Delete_MiddleLines()
    {
        string file = CreateFile("d3.txt", "aaa\nbbb\nccc\nddd\neee\n");
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 4,
        });
        ReadFile(file).Should().Be("aaa\neee\n");
    }

    [Fact]
    public async Task Delete_SingleLineFile()
    {
        string file = CreateFile("d4.txt", "only\n");
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 1,
        });
        ReadFile(file).Should().Be("\n");
    }

    [Fact]
    public async Task Delete_EntireFile()
    {
        string file = CreateFile("d5.txt", "aaa\nbbb\nccc\n");
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 3,
        });
        ReadFile(file).Should().Be("\n");
    }

    [Fact]
    public async Task Delete_OutOfRange_Clamped()
    {
        string file = CreateFile("d6.txt", "aaa\nbbb\n");
        var result = await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 100,
        });
        result.Should().Contain("Deleted 2");
    }
}

// ═══════════════════════════════════════════════════════════════════
// InsertAtLineTool Tests
// ═══════════════════════════════════════════════════════════════════

public class InsertAtLineToolTests : LineEditTestBase
{
    [Fact]
    public async Task Insert_AtBeginning()
    {
        string file = CreateFile("i1.txt", "bbb\nccc\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 1, ["content"] = "aaa",
        });
        ReadFile(file).Should().Be("aaa\nbbb\nccc\n");
    }

    [Fact]
    public async Task Insert_AtMiddle()
    {
        string file = CreateFile("i2.txt", "aaa\nccc\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 2, ["content"] = "bbb",
        });
        ReadFile(file).Should().Be("aaa\nbbb\nccc\n");
    }

    [Fact]
    public async Task Insert_PastEnd_Appends()
    {
        string file = CreateFile("i3.txt", "aaa\nbbb\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 999, ["content"] = "ccc",
        });
        ReadFile(file).Should().Be("aaa\nbbb\nccc\n");
    }

    [Fact]
    public async Task Insert_MultiLineContent()
    {
        string file = CreateFile("i4.txt", "aaa\nddd\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 2, ["content"] = "bbb\nccc",
        });
        ReadFile(file).Should().Be("aaa\nbbb\nccc\nddd\n");
    }

    [Fact]
    public async Task Insert_ContentWithTrailingNewline_NoPhantomLine()
    {
        // THIS IS THE PHANTOM LINE BUG TEST
        // If content = "bbb\n", Split('\n') produces ["bbb", ""] — 2 elements instead of 1.
        // The phantom "" gets inserted as an extra blank line.
        string file = CreateFile("i5.txt", "aaa\nccc\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 2, ["content"] = "bbb\n",
        });
        // Expected: "aaa\nbbb\nccc\n" — NOT "aaa\nbbb\n\nccc\n"
        ReadFile(file).Should().Be("aaa\nbbb\nccc\n");
    }

    [Fact]
    public async Task Insert_MultiLineContentWithTrailingNewline_NoPhantomLine()
    {
        string file = CreateFile("i6.txt", "aaa\nddd\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 2, ["content"] = "bbb\nccc\n",
        });
        // Expected: "aaa\nbbb\nccc\nddd\n" — NOT "aaa\nbbb\nccc\n\nddd\n"
        ReadFile(file).Should().Be("aaa\nbbb\nccc\nddd\n");
    }

    [Fact]
    public async Task Insert_IntoEmptyFile()
    {
        string file = CreateFile("i7.txt", "");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 1, ["content"] = "hello",
        });
        ReadFile(file).Should().Be("hello");
    }

    [Fact]
    public async Task Insert_CrlfContent_IntoCrlfFile()
    {
        string file = CreateFile("i8.txt", "aaa\r\nccc\r\n");
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 2, ["content"] = "bbb",
        });
        ReadFile(file).Should().Be("aaa\r\nbbb\r\nccc\r\n");
    }
}

// ═══════════════════════════════════════════════════════════════════
// ReplaceLinesTool Tests
// ═══════════════════════════════════════════════════════════════════

public class ReplaceLinesToolTests : LineEditTestBase
{
    [Fact]
    public async Task Replace_SameCount()
    {
        string file = CreateFile("r1.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "BBB",
        });
        ReadFile(file).Should().Be("aaa\nBBB\nccc\n");
    }

    [Fact]
    public async Task Replace_FewerLinesThanOriginal()
    {
        // Replacing 3 lines with 1 line — the most common failure mode we experienced
        string file = CreateFile("r2.txt", "aaa\nbbb\nccc\nddd\neee\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 4,
            ["new_content"] = "REPLACED",
        });
        ReadFile(file).Should().Be("aaa\nREPLACED\neee\n");
    }

    [Fact]
    public async Task Replace_MoreLinesThanOriginal()
    {
        string file = CreateFile("r3.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "B1\nB2\nB3",
        });
        ReadFile(file).Should().Be("aaa\nB1\nB2\nB3\nccc\n");
    }

    [Fact]
    public async Task Replace_ContentWithTrailingNewline_NoPhantomLine()
    {
        // THE PRIMARY PHANTOM LINE BUG TEST
        // "REPLACED\n".Split('\n') → ["REPLACED", ""] — phantom empty line
        string file = CreateFile("r4.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "REPLACED\n",
        });
        // Expected: "aaa\nREPLACED\nccc\n" — NOT "aaa\nREPLACED\n\nccc\n"
        ReadFile(file).Should().Be("aaa\nREPLACED\nccc\n");
    }

    [Fact]
    public async Task Replace_MultiLineContentWithTrailingNewline_NoPhantomLine()
    {
        string file = CreateFile("r5.txt", "aaa\nbbb\nccc\nddd\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 3,
            ["new_content"] = "B1\nB2\n",
        });
        // Expected: "aaa\nB1\nB2\nddd\n" — NOT "aaa\nB1\nB2\n\nddd\n"
        ReadFile(file).Should().Be("aaa\nB1\nB2\nddd\n");
    }

    [Fact]
    public async Task Replace_AtBeginning()
    {
        string file = CreateFile("r6.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 1,
            ["new_content"] = "AAA",
        });
        ReadFile(file).Should().Be("AAA\nbbb\nccc\n");
    }

    [Fact]
    public async Task Replace_AtEnd()
    {
        string file = CreateFile("r7.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 3, ["end_line"] = 3,
            ["new_content"] = "CCC",
        });
        ReadFile(file).Should().Be("aaa\nbbb\nCCC\n");
    }

    [Fact]
    public async Task Replace_EntireFile()
    {
        string file = CreateFile("r8.txt", "aaa\nbbb\nccc\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 3,
            ["new_content"] = "new1\nnew2",
        });
        ReadFile(file).Should().Be("new1\nnew2\n");
    }

    [Fact]
    public async Task Replace_InCrlfFile_PreservesLineEndings()
    {
        string file = CreateFile("r9.txt", "aaa\r\nbbb\r\nccc\r\n");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "BBB",
        });
        ReadFile(file).Should().Be("aaa\r\nBBB\r\nccc\r\n");
    }

    [Fact]
    public async Task Replace_SingleLineFileNoNewline()
    {
        string file = CreateFile("r10.txt", "old");
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 1,
            ["new_content"] = "new",
        });
        ReadFile(file).Should().Be("new");
    }
}

// ═══════════════════════════════════════════════════════════════════
// Roundtrip and Multi-Operation Tests
// ═══════════════════════════════════════════════════════════════════

public class LineEditRoundtripTests : LineEditTestBase
{
    [Fact]
    public async Task Roundtrip_ReplaceAndReadBack()
    {
        string original = "class Foo {\n    int x;\n    int y;\n}\n";
        string file = CreateFile("rt1.cs", original);

        // Replace the field declarations (lines 2-3)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 3,
            ["new_content"] = "    string name;",
        });

        ReadFile(file).Should().Be("class Foo {\n    string name;\n}\n");
    }

    [Fact]
    public async Task MultiOp_ReplaceFollowedByInsert()
    {
        string file = CreateFile("rt2.cs", "line1\nline2\nline3\nline4\nline5\n");

        // Replace lines 2-3 with single line
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 3,
            ["new_content"] = "replaced",
        });

        // File should now be: line1\nreplaced\nline4\nline5\n
        // Insert at line 3 (before line4)
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 3, ["content"] = "inserted",
        });

        ReadFile(file).Should().Be("line1\nreplaced\ninserted\nline4\nline5\n");
    }

    [Fact]
    public async Task MultiOp_ConsecutiveReplaces_NoLineDrift()
    {
        // This simulates what happened during the code review:
        // multiple replace operations in sequence, each should target
        // the correct lines based on the file state AFTER previous operations.
        string file = CreateFile("rt3.cs",
            "// comment\n" +
            "using System;\n" +
            "using System.IO;\n" +
            "\n" +
            "class Foo {\n" +
            "    void Bar() { }\n" +
            "}\n");

        // Replace line 2 (using System;)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "using System.Text;",
        });

        // Now file has 7 lines. Replace line 6 (void Bar)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 6, ["end_line"] = 6,
            ["new_content"] = "    void Baz() { }",
        });

        string expected =
            "// comment\n" +
            "using System.Text;\n" +
            "using System.IO;\n" +
            "\n" +
            "class Foo {\n" +
            "    void Baz() { }\n" +
            "}\n";

        ReadFile(file).Should().Be(expected);
    }

    [Fact]
    public async Task MultiOp_ReplaceReducesThenReplace_CorrectLineNumbers()
    {
        // Replace 3 lines with 1, then do another replace on correct line numbers
        string file = CreateFile("rt4.cs",
            "L1\nL2\nL3\nL4\nL5\nL6\n");

        // Replace lines 2-4 with single line → file becomes L1\nX\nL5\nL6\n (4 lines)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 4,
            ["new_content"] = "X",
        });

        ReadFile(file).Should().Be("L1\nX\nL5\nL6\n");

        // Replace line 3 (L5 after the shrink)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 3, ["end_line"] = 3,
            ["new_content"] = "Y",
        });

        ReadFile(file).Should().Be("L1\nX\nY\nL6\n");
    }

    [Fact]
    public async Task MultiOp_ReplaceThenDelete_CorrectResult()
    {
        string file = CreateFile("rt5.cs", "A\nB\nC\nD\nE\n");

        // Replace line 2 with two lines
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 2,
            ["new_content"] = "B1\nB2",
        });

        // File: A\nB1\nB2\nC\nD\nE\n (6 lines)
        // Delete lines 4-5 (C and D)
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 4, ["end_line"] = 5,
        });

        ReadFile(file).Should().Be("A\nB1\nB2\nE\n");
    }

    [Fact]
    public async Task BraceStructure_ReplaceMethodBody_IntactBraces()
    {
        // Simulates Issue #2: replacing a method body containing closing braces
        string file = CreateFile("rt6.cs",
            "class Foo {\n" +
            "    void OldMethod() {\n" +
            "        var x = 1;\n" +
            "        var y = 2;\n" +
            "    }\n" +
            "}\n");

        // Replace lines 2-5 (the entire method including its closing brace)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 5,
            ["new_content"] = "    void NewMethod() {\n        return;\n    }",
        });

        string expected =
            "class Foo {\n" +
            "    void NewMethod() {\n" +
            "        return;\n" +
            "    }\n" +
            "}\n";

        ReadFile(file).Should().Be(expected);
    }

    [Fact]
    public async Task Replace_WithContentEndingInNewline_ThenReplace_NoOrphanedLines()
    {
        // Simulates Issue #3: orphaned code blocks after replacement with trailing \n content
        string file = CreateFile("rt7.cs",
            "L1\nL2\nL3\nL4\nL5\n");

        // Replace lines 2-3 with content ending in \n
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 2, ["end_line"] = 3,
            ["new_content"] = "R1\nR2\n",
        });

        // Verify no orphaned lines
        string afterFirst = ReadFile(file);
        afterFirst.Should().Be("L1\nR1\nR2\nL4\nL5\n");

        // Now replace line 4 (should be L4 after first replace)
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 4, ["end_line"] = 4,
            ["new_content"] = "R3",
        });

        ReadFile(file).Should().Be("L1\nR1\nR2\nR3\nL5\n");
    }

    [Fact]
    public async Task Issue4_InsertAtCorrectScope_AfterPreviousOps()
    {
        // Simulates Issue #4: insert_at_line inserts at wrong scope
        // After a replace that changes line count, subsequent insert should target correct line
        string file = CreateFile("rt8.cs",
            "class Foo {\n" +
            "    void A() { }\n" +
            "    void B() {\n" +
            "        var x = 1;\n" +
            "    }\n" +
            "    void C() { }\n" +
            "}\n");

        // Replace method B (lines 3-5) with a one-liner: 7 lines → 5 lines
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 3, ["end_line"] = 5,
            ["new_content"] = "    void B() { }",
        });

        ReadFile(file).Should().Be(
            "class Foo {\n" +
            "    void A() { }\n" +
            "    void B() { }\n" +
            "    void C() { }\n" +
            "}\n");

        // Insert a new method between B and C (at line 4 — before C)
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 4, ["content"] = "    void D() { }",
        });

        string expected =
            "class Foo {\n" +
            "    void A() { }\n" +
            "    void B() { }\n" +
            "    void D() { }\n" +
            "    void C() { }\n" +
            "}\n";

        ReadFile(file).Should().Be(expected);
    }

    [Fact]
    public async Task FullMultiOpSequence_NoLineDrift()
    {
        // Full multi-operation sequence: replace → insert → replace → delete → verify
        string file = CreateFile("rt9.cs",
            "using System;\n" +
            "\n" +
            "class Program {\n" +
            "    static void Main() {\n" +
            "        Console.WriteLine(\"hello\");\n" +
            "        Console.WriteLine(\"world\");\n" +
            "    }\n" +
            "}\n");

        // Step 1: Replace line 1 (using System;) with two usings
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 1, ["end_line"] = 1,
            ["new_content"] = "using System;\nusing System.IO;",
        });

        // Step 2: Insert a comment at line 4 (before class, now line 4 because line count grew)
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["line_number"] = 4,
            ["content"] = "// Entry point",
        });

        // Step 3: Replace both Console.WriteLines with a single line
        // After steps 1-2, file is 10 lines. The WriteLines are at lines 7-8
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 7, ["end_line"] = 8,
            ["new_content"] = "        Console.WriteLine(\"hello world\");",
        });

        // Step 4: Delete the blank line (line 3)
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file, ["start_line"] = 3, ["end_line"] = 3,
        });

        string expected =
            "using System;\n" +
            "using System.IO;\n" +
            "// Entry point\n" +
            "class Program {\n" +
            "    static void Main() {\n" +
            "        Console.WriteLine(\"hello world\");\n" +
            "    }\n" +
            "}\n";

        ReadFile(file).Should().Be(expected);
    }

    [Fact]
    public async Task PythonParity_DeletePlusInsert_EqualsReplace()
    {
        // Python's replace_lines does delete_lines + insert_at_line internally.
        // Verify that our replace_lines produces the same result.
        string original = "A\nB\nC\nD\nE\n";

        // Method 1: replace_lines directly
        string file1 = CreateFile("parity1.txt", original);
        await ReplaceTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file1, ["start_line"] = 2, ["end_line"] = 4,
            ["new_content"] = "X\nY",
        });

        // Method 2: delete_lines then insert_at_line (Python's approach)
        string file2 = CreateFile("parity2.txt", original);
        await DeleteTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file2, ["start_line"] = 2, ["end_line"] = 4,
        });
        await InsertTool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["relative_path"] = file2, ["line_number"] = 2, ["content"] = "X\nY",
        });

        ReadFile(file1).Should().Be(ReadFile(file2));
        ReadFile(file1).Should().Be("A\nX\nY\nE\n");
    }
}
