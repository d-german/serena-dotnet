using Serena.Lsp.Client;

namespace Serena.Lsp.Tests;

public class NormalizeSymbolNameTests
{
    [Theory]
    [InlineData("LimitLength(string, int) : string", "LimitLength", "(string, int) : string")]
    [InlineData("Name : string", "Name", " : string")]
    [InlineData("GetRequired<T>(IReadOnlyDictionary<string, object?>, string) : T", "GetRequired<T>", "(IReadOnlyDictionary<string, object?>, string) : T")]
    [InlineData("ToolBase(IToolContext, int)", "ToolBase", "(IToolContext, int)")]
    [InlineData("SimpleMethod", "SimpleMethod", null)]
    [InlineData("op_Addition(int, int) : int", "op_Addition", "(int, int) : int")]
    [InlineData("", "", null)]
    public void NormalizeSymbolName_ExtractsBaseNameAndDetail(
        string input, string expectedBaseName, string? expectedDetail)
    {
        var (baseName, detail) = UnifiedSymbolInformation.NormalizeSymbolName(input);

        Assert.Equal(expectedBaseName, baseName);
        Assert.Equal(expectedDetail, detail);
    }
}
