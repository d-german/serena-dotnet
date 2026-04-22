// Tests for TextReplacementHelper.NormalizeLineEndings
// Verifies that MCP JSON strings (\n) are normalized to match file line endings (\r\n or \n).

namespace Serena.Core.Tests;

using Serena.Core.Tools;

public class NormalizeLineEndingsTests
{
    [Fact]
    public void LfNeedle_CrlfContent_UpgradesToCrlf()
    {
        string content = "line1\r\nline2\r\nline3\r\n";
        string needle = "line1\nline2";

        string result = TextReplacementHelper.NormalizeLineEndings(needle, content);

        Assert.Equal("line1\r\nline2", result);
    }

    [Fact]
    public void CrlfNeedle_LfContent_DowngradesToLf()
    {
        string content = "line1\nline2\nline3\n";
        string needle = "line1\r\nline2";

        string result = TextReplacementHelper.NormalizeLineEndings(needle, content);

        Assert.Equal("line1\nline2", result);
    }

    [Fact]
    public void MatchingLineEndings_NoChange()
    {
        string content = "line1\r\nline2\r\n";
        string needle = "line1\r\nline2";

        string result = TextReplacementHelper.NormalizeLineEndings(needle, content);

        Assert.Equal("line1\r\nline2", result);
    }

    [Fact]
    public void SingleLineText_NoChange()
    {
        string content = "line1\r\nline2\r\n";
        string needle = "line1";

        string result = TextReplacementHelper.NormalizeLineEndings(needle, content);

        Assert.Equal("line1", result);
    }

    [Fact]
    public void EmptyText_NoChange()
    {
        string content = "line1\r\nline2\r\n";

        Assert.Equal("", TextReplacementHelper.NormalizeLineEndings("", content));
    }

    [Fact]
    public void MultipleNewlines_AllNormalized()
    {
        string content = "a\r\nb\r\nc\r\n";
        string needle = "a\nb\nc";

        string result = TextReplacementHelper.NormalizeLineEndings(needle, content);

        Assert.Equal("a\r\nb\r\nc", result);
    }
}
