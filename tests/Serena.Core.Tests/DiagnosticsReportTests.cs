// Diagnostics formatting, the inline-refresh readiness gate, and the JSON
// schema for object-array tool parameters.

using System.Text.Json;
using FluentAssertions;
using Serena.Core.Project;
using Serena.Core.Tools;
using Serena.Lsp;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Tests;

public class DiagnosticsReportTests
{
    private static FileDiagnostic D(int line, int ch, DiagnosticSeverity severity, string code, string message) =>
        new(line, ch, line, ch + 1, severity, code, message, "csharp");

    [Fact]
    public void Format_CountsAllSeverities_ListsAtOrAboveMinimum_OneBased()
    {
        var diagnostics = new List<FileDiagnostic>
        {
            D(11, 4, DiagnosticSeverity.Warning, "CS0168", "unused variable"),
            D(6, 0, DiagnosticSeverity.Error, "CS1513", "} expected"),
            D(2, 8, DiagnosticSeverity.Information, "IDE0005", "unnecessary using"),
        };

        string text = DiagnosticsReport.Format("src/A.cs", diagnostics, DiagnosticSeverity.Warning);

        text.Should().Be(
            "src/A.cs: 1 error(s), 1 warning(s) (showing 2 at or above warning)\n" +
            "  error CS1513 (7,1): } expected\n" +
            "  warning CS0168 (12,5): unused variable");
    }

    [Fact]
    public void Format_ErrorsOnly_ByDefaultForEditTools_AndMultiLineMessagesStayOnOneLine()
    {
        var diagnostics = new List<FileDiagnostic>
        {
            D(0, 0, DiagnosticSeverity.Error, "CS0246", "The type or namespace name 'Foo'\ncould not be found"),
            D(0, 0, DiagnosticSeverity.Warning, "CS8019", "unnecessary"),
        };

        string text = DiagnosticsReport.Format("B.cs", diagnostics, DiagnosticsReport.ParseMinSeverity("error"));

        text.Should().Contain("  error CS0246 (1,1): The type or namespace name 'Foo' could not be found");
        text.Should().NotContain("CS8019");
    }

    [Fact]
    public void Format_NoDiagnostics_IsOneSummaryLine()
    {
        DiagnosticsReport.Format("C.cs", [], DiagnosticSeverity.Warning)
            .Should().Be("C.cs: 0 error(s), 0 warning(s)");
    }

    [Theory]
    [InlineData("error", DiagnosticSeverity.Error)]
    [InlineData("Warnings", DiagnosticSeverity.Warning)]
    [InlineData("info", DiagnosticSeverity.Information)]
    [InlineData("all", DiagnosticSeverity.Hint)]
    [InlineData("", DiagnosticSeverity.Warning)]
    [InlineData(null, DiagnosticSeverity.Warning)]
    public void ParseMinSeverity_AcceptsCommonSpellings(string? text, DiagnosticSeverity expected)
    {
        DiagnosticsReport.ParseMinSeverity(text).Should().Be(expected);
    }

    [Theory]
    [InlineData(WorkspaceReadyState.Ready, true)]
    [InlineData(WorkspaceReadyState.Partial, true)]
    [InlineData(WorkspaceReadyState.Loading, false)]
    [InlineData(WorkspaceReadyState.NotStarted, false)]
    [InlineData(WorkspaceReadyState.Failed, false)]
    public void InlineRefresh_RunsInPartialAsWellAsReady(WorkspaceReadyState state, bool expected)
    {
        LanguageServerManager.CanRefreshInline(state).Should().Be(expected);
    }

    [Fact]
    public void Schema_ForObjectArrayParameter_EmitsItemsOfTypeObject()
    {
        var tool = new ApplyEditsTool(NullToolContext.Instance);
        using var schema = ToolBase.GenerateJsonSchema(tool.Parameters);

        var edits = schema.RootElement.GetProperty("properties").GetProperty("edits");
        edits.GetProperty("type").GetString().Should().Be("array");
        edits.GetProperty("items").GetProperty("type").GetString().Should().Be("object");
        schema.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("edits");
    }

    [Fact]
    public void Schema_ForPrimitiveArrayParameter_IsUnchanged()
    {
        var tool = new FindSymbolTool(NullToolContext.Instance);
        using var schema = ToolBase.GenerateJsonSchema(tool.Parameters);

        var kinds = schema.RootElement.GetProperty("properties").GetProperty("include_kinds");
        kinds.GetProperty("items").GetProperty("type").GetString().Should().Be("integer");
    }
}
