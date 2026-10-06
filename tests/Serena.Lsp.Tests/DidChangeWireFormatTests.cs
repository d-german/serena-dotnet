using FluentAssertions;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

/// <summary>
/// didChange replaces a document's whole previous text through a range ending at
/// <see cref="LspClient.EndPosition"/>, so that position must be exactly where the server
/// thinks the text ends: short of it leaves a stale tail, past it makes Roslyn throw.
/// </summary>
public sealed class DidChangeWireFormatTests
{
    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("class A {}", 0, 10)]
    [InlineData("a\nbc", 1, 2)]
    [InlineData("a\r\nbc\r\n", 2, 0)]
    [InlineData("a\rb", 1, 1)]
    [InlineData("a\u2028bc", 1, 2)]
    [InlineData("a\u2029b", 1, 1)]
    public void EndPosition_CountsEveryLineBreakTheServerCounts(string text, int line, int character)
    {
        LspClient.EndPosition(text, Language.CSharp).Should().Be(new Position(line, character));
        LspClient.EndPosition(text, Language.TypeScript).Should().Be(new Position(line, character));
    }

    [Fact]
    public void EndPosition_TreatsNextLineAsABreakOnlyForCSharp()
    {
        LspClient.EndPosition("a\u0085bc", Language.CSharp).Should().Be(new Position(1, 2));
        LspClient.EndPosition("a\u0085bc", Language.TypeScript).Should().Be(new Position(0, 4));
    }
}
