using FluentAssertions;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;
using LspRange = Serena.Lsp.Protocol.Types.Range;

namespace Serena.Lsp.Tests;

public sealed class FrameworkConditionalSymbolsTests
{
    [Theory]
    [InlineData("#if NETFRAMEWORK\nclass A {}\n#endif")]
    [InlineData("  #if !NET8_0_OR_GREATER\nclass A {}\n#endif")]
    [InlineData("#if DEBUG\n#elif NET48\n#endif")]
    [InlineData("#if NET\nclass A {}\n#else\nclass B {}\n#endif")]
    [InlineData("#if NETSTANDARD || NETCOREAPP\n#endif")]
    public void HasFrameworkConditional_DetectsTargetFrameworkTests(string source)
    {
        FrameworkConditionalSymbols.HasFrameworkConditional(source).Should().BeTrue();
    }

    [Theory]
    [InlineData("#if DEBUG\nclass A {}\n#endif")]
    [InlineData("#if NETWORK_FEATURE\nclass A {}\n#endif")]
    [InlineData("// #if NETFRAMEWORK\nclass A {}")]
    [InlineData("class NETFRAMEWORK {}")]
    public void HasFrameworkConditional_IgnoresOtherConditionsAndText(string source)
    {
        FrameworkConditionalSymbols.HasFrameworkConditional(source).Should().BeFalse();
    }

    [Fact]
    public void HeaderLineCount_CountsOnlyDirectives_SoTheShiftIsExact()
    {
        FrameworkConditionalSymbols.HeaderLineCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void ShiftLines_MovesEveryRangeAndKeepsTheTree()
    {
        var cls = Symbol("Proxy", SymbolKind.Class, 50, 90, Symbol("Open", SymbolKind.Method, 60, 70));

        var shifted = FrameworkConditionalSymbols.ShiftLines([cls], -46);

        shifted.Should().ContainSingle();
        shifted[0].BodyRange!.Start.Line.Should().Be(4);
        shifted[0].SelectionRange!.Start.Line.Should().Be(4);
        shifted[0].Location!.Range.End.Line.Should().Be(44);
        shifted[0].Children.Should().ContainSingle()
            .Which.Should().Match<UnifiedSymbolInformation>(c => c.StartLine == 14 && c.Parent == shifted[0]);
        cls.BodyRange!.Start.Line.Should().Be(50, "the input is copied, not modified");
    }

    [Fact]
    public void Merge_AddsTypesOnlyOneBuildCompiles_InDocumentOrder()
    {
        var modern = new List<UnifiedSymbolInformation> { Symbol("Shared", SymbolKind.Class, 40, 60) };
        var framework = new List<UnifiedSymbolInformation>
        {
            Symbol("FrameworkOnly", SymbolKind.Class, 2, 30),
            Symbol("Shared", SymbolKind.Class, 40, 60),
        };

        var merged = FrameworkConditionalSymbols.Merge(modern, framework);

        merged.Select(s => s.Name).Should().Equal("FrameworkOnly", "Shared");
    }

    [Fact]
    public void Merge_CombinesMembersOfTheSameType_AndRenumbersOverloads()
    {
        var modern = new List<UnifiedSymbolInformation>
        {
            Symbol("Service", SymbolKind.Class, 0, 100,
                Symbol("Send", SymbolKind.Method, 10, 20),
                Symbol("Close", SymbolKind.Method, 50, 60)),
        };
        var framework = new List<UnifiedSymbolInformation>
        {
            Symbol("Service", SymbolKind.Class, 0, 100,
                Symbol("Send", SymbolKind.Method, 10, 20),
                Symbol("Send", SymbolKind.Method, 30, 40),
                Symbol("Close", SymbolKind.Method, 50, 60)),
        };

        var merged = FrameworkConditionalSymbols.Merge(modern, framework);

        var members = merged.Should().ContainSingle().Which.Children;
        members.Select(m => (m.Name, m.StartLine, m.OverloadIndex)).Should().Equal(
            ("Send", 10, 0), ("Send", 30, 1), ("Close", 50, (int?)null));
        members.Should().OnlyContain(m => m.Parent == merged[0]);
    }

    [Fact]
    public void Merge_KeepsBothDeclarationsWhenEachBuildDeclaresTheTypeDifferently()
    {
        var modern = new List<UnifiedSymbolInformation> { Symbol("Channel", SymbolKind.Class, 20, 30) };
        var framework = new List<UnifiedSymbolInformation> { Symbol("Channel", SymbolKind.Class, 5, 15) };

        var merged = FrameworkConditionalSymbols.Merge(modern, framework);

        merged.Select(s => s.StartLine).Should().Equal(5, 20);
    }

    private static UnifiedSymbolInformation Symbol(
        string name, SymbolKind kind, int startLine, int endLine, params UnifiedSymbolInformation[] children)
    {
        var range = new LspRange(new Position(startLine, 4), new Position(endLine, 5));
        var symbol = new UnifiedSymbolInformation
        {
            Name = name,
            Kind = kind,
            BodyRange = range,
            SelectionRange = new LspRange(new Position(startLine, 10), new Position(startLine, 10 + name.Length)),
            Location = new Location("file:///C:/repo/File.cs", range),
        };
        foreach (var child in children)
        {
            child.Parent = symbol;
            symbol.Children.Add(child);
        }
        return symbol;
    }
}
