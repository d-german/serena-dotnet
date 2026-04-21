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
}
