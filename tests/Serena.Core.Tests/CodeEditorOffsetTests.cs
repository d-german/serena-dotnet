using FluentAssertions;
using Serena.Core.Editor;

namespace Serena.Core.Tests;

/// <summary>
/// Tests for GetOffset with both CRLF and LF line endings.
/// Verifies the fix for the ReadAllLinesAsync mismatch bug where
/// offset drifted by 1 per line on Windows (\r\n) files.
/// </summary>
public class CodeEditorOffsetTests
{
    [Fact]
    public void GetOffset_LF_FirstLine_ReturnsColumn()
    {
        string content = "line0\nline1\nline2\n";
        string[] lines = content.Split('\n');

        var offset = LanguageServerCodeEditor.GetOffset(lines, 0, 3);

        offset.Should().Be(3);
        content[offset].Should().Be('e');
    }

    [Fact]
    public void GetOffset_LF_SecondLine_ReturnsCorrectOffset()
    {
        string content = "line0\nline1\nline2\n";
        string[] lines = content.Split('\n');

        // Line 1, col 0 should be at index 6 ("line0\n" = 6 chars)
        var offset = LanguageServerCodeEditor.GetOffset(lines, 1, 0);

        offset.Should().Be(6);
        content[offset].Should().Be('l');
    }

    [Fact]
    public void GetOffset_LF_ThirdLine_ReturnsCorrectOffset()
    {
        string content = "line0\nline1\nline2\n";
        string[] lines = content.Split('\n');

        // Line 2, col 0 should be at index 12 ("line0\nline1\n" = 12 chars)
        var offset = LanguageServerCodeEditor.GetOffset(lines, 2, 0);

        offset.Should().Be(12);
        content[offset].Should().Be('l');
    }

    [Fact]
    public void GetOffset_CRLF_FirstLine_ReturnsColumn()
    {
        string content = "line0\r\nline1\r\nline2\r\n";
        string[] lines = content.Split('\n');
        // lines = ["line0\r", "line1\r", "line2\r", ""]

        var offset = LanguageServerCodeEditor.GetOffset(lines, 0, 3);

        offset.Should().Be(3);
        content[offset].Should().Be('e');
    }

    [Fact]
    public void GetOffset_CRLF_SecondLine_ReturnsCorrectOffset()
    {
        string content = "line0\r\nline1\r\nline2\r\n";
        string[] lines = content.Split('\n');
        // lines = ["line0\r", "line1\r", "line2\r", ""]
        // line0\r has length 6, +1 for \n = 7 chars to get to line1

        var offset = LanguageServerCodeEditor.GetOffset(lines, 1, 0);

        offset.Should().Be(7);
        content[offset].Should().Be('l');
    }

    [Fact]
    public void GetOffset_CRLF_ThirdLine_ReturnsCorrectOffset()
    {
        string content = "line0\r\nline1\r\nline2\r\n";
        string[] lines = content.Split('\n');

        var offset = LanguageServerCodeEditor.GetOffset(lines, 2, 0);

        offset.Should().Be(14);
        content[offset].Should().Be('l');
    }

    [Fact]
    public void GetOffset_CRLF_ColumnOnSecondLine_SkipsCR()
    {
        // LSP columns don't count \r, so col 0 on line 1 points to 'l' in "line1"
        // The \r is part of the previous line element after Split('\n')
        string content = "hello\r\nworld\r\n";
        string[] lines = content.Split('\n');

        var offset = LanguageServerCodeEditor.GetOffset(lines, 1, 0);

        offset.Should().Be(7); // "hello\r\n" = 7 chars
        content[offset].Should().Be('w');
    }

    [Fact]
    public void GetOffset_CRLF_ManyLines_NoAccumulatedDrift()
    {
        // This is the exact scenario that was broken before the fix:
        // On a 17-line CRLF file, offset was off by 17 chars
        var lineTexts = Enumerable.Range(0, 20)
            .Select(i => $"    line {i:D2} content here")
            .ToArray();
        string content = string.Join("\r\n", lineTexts) + "\r\n";
        string[] lines = content.Split('\n');

        // Check line 17 — was the most visibly broken case
        var offset = LanguageServerCodeEditor.GetOffset(lines, 17, 0);

        // Each line is "    line XX content here\r" (25 chars) + \n (1 char) = 26 per line
        int expectedOffset = 17 * 26;
        offset.Should().Be(expectedOffset);
        content[offset..].Should().StartWith("    line 17");
    }

    [Fact]
    public void GetOffset_MixedLineEndings_HandlesCorrectly()
    {
        // Mix of \r\n and \n in same file
        string content = "line0\r\nline1\nline2\r\nline3\n";
        string[] lines = content.Split('\n');
        // lines = ["line0\r", "line1", "line2\r", "line3", ""]

        // Line 0, col 0 → 0
        LanguageServerCodeEditor.GetOffset(lines, 0, 0).Should().Be(0);

        // Line 1 → after "line0\r\n" = 7
        LanguageServerCodeEditor.GetOffset(lines, 1, 0).Should().Be(7);
        content[7].Should().Be('l');

        // Line 2 → after "line0\r\nline1\n" = 7 + 6 = 13
        LanguageServerCodeEditor.GetOffset(lines, 2, 0).Should().Be(13);
        content[13].Should().Be('l');

        // Line 3 → after "line0\r\nline1\nline2\r\n" = 13 + 7 = 20
        LanguageServerCodeEditor.GetOffset(lines, 3, 0).Should().Be(20);
        content[20].Should().Be('l');
    }

    [Fact]
    public void GetOffset_EmptyFile_ReturnsZero()
    {
        string content = "";
        string[] lines = content.Split('\n');

        var offset = LanguageServerCodeEditor.GetOffset(lines, 0, 0);

        offset.Should().Be(0);
    }

    [Fact]
    public void GetOffset_SingleLineNoNewline_ReturnsColumn()
    {
        string content = "hello";
        string[] lines = content.Split('\n');

        var offset = LanguageServerCodeEditor.GetOffset(lines, 0, 3);

        offset.Should().Be(3);
        content[offset].Should().Be('l');
    }

    [Fact]
    public void GetOffset_ColumnClamped_ToLineLength()
    {
        string content = "short\nline\n";
        string[] lines = content.Split('\n');

        // Column beyond line length should clamp
        var offset = LanguageServerCodeEditor.GetOffset(lines, 0, 100);

        offset.Should().Be(5); // "short".Length
    }

    [Fact]
    public void GetOffset_CRLF_ReplacementScenario_ProducesCorrectSlice()
    {
        // Simulate the exact replace_symbol_body scenario that was corrupted:
        // A method body from line 3 col 0 to line 5 col 1 (closing brace)
        string content = "using System;\r\n\r\npublic class Foo\r\n{\r\n    public int Bar()\r\n    {\r\n        return 42;\r\n    }\r\n}\r\n";
        string[] lines = content.Split('\n');

        // Body of Bar() method: from "{" to "}" inclusive
        // Line 5 = "    {\r", line 7 = "    }\r"
        int startOffset = LanguageServerCodeEditor.GetOffset(lines, 5, 4); // the '{'
        int endOffset = LanguageServerCodeEditor.GetOffset(lines, 7, 5);   // after '}'

        string body = content[startOffset..endOffset];
        body.Should().Contain("return 42");
        body.Should().StartWith("{");
        body.Should().EndWith("}");

        // Now replace it
        string newBody = "=> 99;";
        string newContent = content[..startOffset] + newBody + content[endOffset..];

        newContent.Should().Contain("=> 99;");
        newContent.Should().NotContain("return 42");
        // Verify no leftover fragments
        newContent.Should().Contain("public int Bar()");
    }
}
