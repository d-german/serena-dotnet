// The parser-built outline must match the shape Roslyn's language server returns,
// since both feed the same cache and the same tools.

using FluentAssertions;
using Serena.Lsp.Client;
using Serena.Lsp.Outline;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

public class CSharpSyntaxOutlineTests
{
    private const string Sample = """
        using System;

        namespace Shared.Types;

        /// <summary>A result.</summary>
        public abstract record Result<T>
        {
            public required string SourceId { get; init; }
            public const int Limit = 3;
            private readonly Lazy<int> _lazy, _other;
            public event EventHandler? Changed;

            protected Result([CallerMemberName] string callerMemberName = "")
            {
            }

            public static Result<T> ToResult<T>(this T value, string sourceId) => throw null!;
            public void Dispose(bool disposing) { }
            public void Dispose() { }
            IDisposable IHolder.Inner => throw null!;
            public string this[string key] => key;
            public static implicit operator T?(Result<T> result) => default;
            public static bool operator ==(Result<T> a, Result<T> b) => true;

            public sealed class Nested { }
        }

        public interface IHolder { IDisposable Inner { get; } }
        public readonly record struct Point(int X, int Y);
        public enum Mode { Fast, Slow }
        public delegate int Handler(string text);
        """;

    [Fact]
    public void Build_ProducesTheServerShape()
    {
        var symbols = CSharpSyntaxOutline.Build(@"C:\repo\Result.cs", Sample);

        symbols.Should().ContainSingle();
        var ns = symbols[0];
        ns.Name.Should().Be("Shared.Types");
        ns.Kind.Should().Be(SymbolKind.Namespace);
        ns.Detail.Should().Be("Shared.Types");
        ns.Location!.Uri.Should().Be("file:///C:/repo/Result.cs");
        ns.Children.Select(c => (c.Name, c.Kind)).Should().Equal(
            ("Result<T>", SymbolKind.Class),
            ("IHolder", SymbolKind.Interface),
            ("Point", SymbolKind.Struct),
            ("Mode", SymbolKind.Enum),
            ("Handler", SymbolKind.Class));

        var result = ns.Children[0];
        result.Detail.Should().Be("Result<T>");
        result.Parent.Should().BeSameAs(ns);
        result.StartLine.Should().Be(5, "the range starts at the declaration, after the doc comment");
        result.SelectionRange!.Start.Should().Be(new Position(5, 23));

        var members = result.Children.Select(c => (c.Name, c.Kind, c.Detail, c.OverloadIndex)).ToList();
        members.Should().Contain(("SourceId", SymbolKind.Property, "SourceId : string", null));
        members.Should().Contain(("Limit", SymbolKind.Constant, "Limit : int", null));
        members.Should().Contain(("_lazy", SymbolKind.Field, "_lazy : Lazy<int>", null));
        members.Should().Contain(("_other", SymbolKind.Field, "_other : Lazy<int>", null));
        members.Should().Contain(("Changed", SymbolKind.Event, "Changed : EventHandler?", null));
        members.Should().Contain(("Result", SymbolKind.Method, "Result(string)", null), "constructors are methods without a return type");
        members.Should().Contain(("ToResult<T>", SymbolKind.Method, "ToResult<T>(T, string) : Result<T>", null), "the extension `this` is left out");
        members.Should().Contain(("Dispose", SymbolKind.Method, "Dispose(bool) : void", 0));
        members.Should().Contain(("Dispose", SymbolKind.Method, "Dispose() : void", 1));
        members.Should().Contain(("Inner", SymbolKind.Property, "Inner : IDisposable", null), "explicit implementations carry the plain member name, as the server reports them");
        members.Should().Contain(("this[string]", SymbolKind.Property, "this[string] : string", null));
        members.Should().Contain(("implicit operator T?", SymbolKind.Operator, "implicit operator T?(Result<T>)", null));
        members.Should().Contain(("operator ==", SymbolKind.Operator, "operator ==(Result<T>, Result<T>) : bool", null));
        members.Should().Contain(("Nested", SymbolKind.Class, "Nested", null));

        var lazy = result.Children.Single(c => c.Name == "_lazy");
        lazy.StartLine.Should().Be(lazy.SelectionRange!.Start.Line);
        lazy.BodyRange!.Start.Should().Be(lazy.SelectionRange!.Start, "a field's range is its declarator, as the server reports it");

        ns.Children.Single(c => c.Name == "Mode").Children.Select(c => (c.Name, c.Kind)).Should().Equal(
            ("Fast", SymbolKind.EnumMember), ("Slow", SymbolKind.EnumMember));
        ns.Children.Single(c => c.Name == "Handler").Detail.Should().Be("Handler(string) : int");

        result.Children.Single(c => c.Name == "Dispose" && c.OverloadIndex == 1).NamePath.Should().Be("Shared.Types/Result<T>/Dispose[1]");
    }

    [Fact]
    public void Build_ListsLocalFunctionsUnderTheirMember_AndStripsVerbatimPrefixes()
    {
        const string source = """
            class Worker
            {
                public string @operator { get; set; }
                public int Run(int n)
                {
                    int Twice(int x) { int Inner(int y) => y; return Inner(x) * 2; }
                    Func<int, int> lambda = v => { int Hidden() => v; return Hidden(); };
                    return Twice(n);
                }
            }
            """;

        var worker = CSharpSyntaxOutline.Build(@"C:
epo\Worker.cs", source).Single();

        worker.Children.Select(c => c.Name).Should().Equal("operator", "Run");
        var run = worker.Children[1];
        run.Children.Select(c => (c.Name, c.Detail)).Should().Equal(("Twice", "Twice(int) : int"), ("Hidden", "Hidden() : int"));
        run.Children[0].Children.Select(c => c.Name).Should().Equal("Inner");

    }

    [Fact]
    public void Build_MergesDeclarationsThatOnlyCompileForNetFramework()
    {
        const string source = """
            namespace Multi
            {
                public class Both
                {
            #if NETFRAMEWORK
                    public void OnlyFramework() { }
            #else
                    public void OnlyModern() { }
            #endif
                    public void Always() { }
                }
            #if NET48
                public class FrameworkOnly { }
            #endif
            }
            """;

        var symbols = CSharpSyntaxOutline.Build(@"C:\repo\Multi.cs", source);

        var ns = symbols.Single();
        ns.Children.Select(c => c.Name).Should().Equal("Both", "FrameworkOnly");
        ns.Children[0].Children.Select(c => c.Name).Should().Equal("OnlyFramework", "OnlyModern", "Always");
    }

    [Fact]
    public void Build_KeepsInactiveModernOnlyCodeOutOfTheOutline_WithoutFrameworkConditionals()
    {
        const string source = """
            public class Plain
            {
            #if DEBUG
                public void DebugOnly() { }
            #endif
            #if SOMETHING_ELSE
                public void Never() { }
            #endif
            }
            """;

        var plain = CSharpSyntaxOutline.Build(@"C:\repo\Plain.cs", source).Single();

        plain.Children.Select(c => c.Name).Should().Equal("DebugOnly");
    }

    [Fact]
    public void Build_OutlinesTopLevelTypesWithoutANamespace_AndIgnoresTopLevelStatements()
    {
        const string source = """
            using System;
            Console.WriteLine("hi");
            class Program { static void Main() { } }
            """;

        var symbols = CSharpSyntaxOutline.Build(@"C:\repo\Program.cs", source);

        symbols.Select(s => s.Name).Should().Equal("Program");
        symbols[0].Children.Single().Detail.Should().Be("Main() : void");
    }

    [Fact]
    public void TryBuild_ReturnsNullForAMissingFile()
    {
        CSharpSyntaxOutline.TryBuild(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cs")).Should().BeNull();
    }
}
