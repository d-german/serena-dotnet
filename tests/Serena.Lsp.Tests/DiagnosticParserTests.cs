// Parsing of pull-model diagnostic reports and push-model publish notifications.

using FluentAssertions;
using Newtonsoft.Json.Linq;
using Serena.Lsp;
using Serena.Lsp.Protocol.Types;

namespace Serena.Lsp.Tests;

public class DiagnosticParserTests
{
    private const string FullReport = """
        {
          "kind": "full",
          "resultId": "7",
          "items": [
            {
              "range": { "start": { "line": 6, "character": 4 }, "end": { "line": 6, "character": 9 } },
              "severity": 1,
              "code": "CS0246",
              "source": "csharp",
              "message": "The type or namespace name 'Foo' could not be found"
            },
            {
              "range": { "start": { "line": 1, "character": 0 }, "end": { "line": 1, "character": 20 } },
              "severity": 4,
              "code": { "value": "IDE0005", "target": "https://example" },
              "message": "Using directive is unnecessary.",
              "tags": [1]
            }
          ]
        }
        """;

    [Fact]
    public void ParsePullReport_FullReport_ReturnsEveryItem()
    {
        var items = DiagnosticParser.ParsePullReport(JToken.Parse(FullReport));

        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        items[0].Should().Be(new FileDiagnostic(6, 4, 6, 9, DiagnosticSeverity.Error, "CS0246",
            "The type or namespace name 'Foo' could not be found", "csharp"));
        items[1].Code.Should().Be("IDE0005", "an object-valued code reports its value");
        items[1].Severity.Should().Be(DiagnosticSeverity.Hint);
    }

    [Fact]
    public void ParsePullReport_Unchanged_ReturnsNull_SoTheCallerFallsBack()
    {
        DiagnosticParser.ParsePullReport(JToken.Parse("""{ "kind": "unchanged", "resultId": "7" }""")).Should().BeNull();
        DiagnosticParser.ParsePullReport(null).Should().BeNull();
        DiagnosticParser.ParsePullReport(JToken.Parse("null")).Should().BeNull();
    }

    [Fact]
    public void ParseItems_PublishedDiagnostics_DefaultsSeverityToError_AndSkipsMalformedEntries()
    {
        var token = JToken.Parse("""
            [
              { "range": { "start": { "line": 3, "character": 2 }, "end": { "line": 3, "character": 5 } }, "message": "no severity" },
              { "message": "no range" },
              { "range": { "start": { "line": 0, "character": 0 } } }
            ]
            """);

        var items = DiagnosticParser.ParseItems(token);

        items.Should().ContainSingle();
        items[0].Severity.Should().Be(DiagnosticSeverity.Error);
        items[0].Code.Should().BeNull();
        items[0].EndLine.Should().Be(3);
    }

    [Fact]
    public void ParseItems_NonArray_IsEmpty()
    {
        DiagnosticParser.ParseItems(JToken.Parse("{}")).Should().BeEmpty();
        DiagnosticParser.ParseItems(null).Should().BeEmpty();
    }
}
