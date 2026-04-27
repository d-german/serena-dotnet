// ToolResultFormatterTests - Phase D3

namespace Serena.Core.Tests;

using Serena.Core.Tools;

public class ToolResultFormatterTests
{
    [Fact]
    public void Format_ShortString_ReturnsUnchanged()
    {
        string result = ToolResultFormatter.Format("Hello", maxChars: 100);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void Format_LongString_Truncates()
    {
        string input = new('x', 200);
        string result = ToolResultFormatter.Format(input, maxChars: 100);

        Assert.True(result.Length <= 100);
        Assert.Contains("truncated", result);
        Assert.Contains("200 total chars", result);
    }

    [Fact]
    public void FormatCollection_SmallList_ReturnsAll()
    {
        var items = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "a" },
            new() { ["name"] = "b" },
        };

        string result = ToolResultFormatter.FormatCollection(items, maxChars: 10_000);
        Assert.Contains("\"a\"", result);
        Assert.Contains("\"b\"", result);
    }

    [Fact]
    public void FormatCollection_LargeList_TruncatesItems()
    {
        var items = Enumerable.Range(0, 100)
            .Select(i => new Dictionary<string, object?> { ["name"] = new string('x', 100) })
            .ToList();

        string result = ToolResultFormatter.FormatCollection(items, maxChars: 500);
        Assert.Contains("truncated", result);
    }

    [Fact]
    public void FormatSymbols_WithBody_StripsBodyOnOverflow()
    {
        var symbols = new List<Dictionary<string, object?>>
        {
            new()
            {
                ["name_path"] = "MyClass",
                ["kind"] = "Class",
                ["body"] = new string('x', 5000),
            },
            new()
            {
                ["name_path"] = "MyMethod",
                ["kind"] = "Method",
                ["body"] = new string('y', 5000),
            },
        };

        string result = ToolResultFormatter.FormatSymbols(symbols, maxChars: 500);

        // Body should be stripped in shortened version
        Assert.DoesNotContain(new string('x', 5000), result);
        Assert.Contains("MyClass", result);
    }

    [Fact]
    public void LimitLength_WithinLimit_ReturnsOriginal()
    {
        Assert.Equal("hello", ToolResultFormatter.LimitLength("hello", 100));
    }

    [Fact]
    public void LimitLength_ExceedsLimit_Truncates()
    {
        string result = ToolResultFormatter.LimitLength(new string('a', 200), 100);
        Assert.True(result.Length <= 100);
        Assert.Contains("truncated", result);
    }

    private static Dictionary<string, object?> Sym(string namePath, string kind, string relPath, int startLine, int endLine, string body) =>
        new()
        {
            ["name_path"] = namePath,
            ["kind"] = kind,
            ["relative_path"] = relPath,
            ["body_location"] = new { start_line = startLine, end_line = endLine },
            ["body"] = body,
        };

    [Fact]
    public void FormatSymbolsOutlineFirst_AllBodiesFit_InlinesAll()
    {
        var symbols = new List<Dictionary<string, object?>>
        {
            Sym("Foo/Bar", "Method", "Foo.cs", 0, 2, "body-A"),
            Sym("Foo/Baz", "Method", "Foo.cs", 4, 6, "body-B"),
        };

        string result = ToolResultFormatter.FormatSymbolsOutlineFirst(
            symbols, maxBodyBytes: 1024, maxTotalBodyBytes: 4096);

        Assert.Contains("=== Outline ===", result);
        Assert.Contains("=== Bodies ===", result);
        Assert.Contains("Method Foo/Bar Foo.cs:1-3 (6 chars)", result);
        Assert.Contains("Method Foo/Baz Foo.cs:5-7 (6 chars)", result);
        Assert.Contains("body-A", result);
        Assert.Contains("body-B", result);
        Assert.DoesNotContain("body omitted", result);
    }

    [Fact]
    public void FormatSymbolsOutlineFirst_OneBodyOverPerBodyCap_OmitsWithHint()
    {
        string huge = new('x', 5000);
        var symbols = new List<Dictionary<string, object?>>
        {
            Sym("A", "Method", "x.cs", 0, 0, "small"),
            Sym("B", "Method", "x.cs", 5, 5, huge),
        };

        string result = ToolResultFormatter.FormatSymbolsOutlineFirst(
            symbols, maxBodyBytes: 1024, maxTotalBodyBytes: 8192);

        Assert.Contains("=== Outline ===", result);
        Assert.Contains("Method A x.cs:1-1 (5 chars)", result);
        Assert.Contains("Method B x.cs:6-6 (5000 chars)", result);
        Assert.Contains("small", result);
        Assert.Contains("body omitted: 5000 chars", result);
        Assert.Contains("name_path=\"B\"", result);
        Assert.Contains("relative_path=\"x.cs\"", result);
        Assert.Contains("include_body=true", result);
        Assert.DoesNotContain(huge, result);
    }

    [Fact]
    public void FormatSymbolsOutlineFirst_TotalCapExceeded_OmitsRemainder()
    {
        string medium = new('y', 600);
        var symbols = new List<Dictionary<string, object?>>
        {
            Sym("A", "Method", "x.cs", 0, 0, medium),
            Sym("B", "Method", "x.cs", 1, 1, medium),
            Sym("C", "Method", "x.cs", 2, 2, medium),
        };

        // perBody=1024 (each medium body fits), totalCap=1000 (only first fits)
        string result = ToolResultFormatter.FormatSymbolsOutlineFirst(
            symbols, maxBodyBytes: 1024, maxTotalBodyBytes: 1000);

        Assert.Contains("=== Outline ===", result);
        Assert.Contains("Method A x.cs:1-1 (600 chars)", result);
        Assert.Contains("Method B x.cs:2-2 (600 chars)", result);
        Assert.Contains("Method C x.cs:3-3 (600 chars)", result);
        // First body inlined
        Assert.Contains(medium, result);
        // Subsequent bodies omitted with hint
        Assert.Contains("name_path=\"B\"", result);
        Assert.Contains("name_path=\"C\"", result);
        int omittedCount = System.Text.RegularExpressions.Regex.Matches(result, "body omitted").Count;
        Assert.Equal(2, omittedCount);
    }
}
