// v1.0.26: get_language_server_status tool tests.

using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class LanguageServerStatusToolTests
{
    [Fact]
    public async Task NoLanguageServers_ReturnsCSharpNotStarted()
    {
        var tool = new LanguageServerStatusTool(NullToolContext.Instance);
        string output = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        // CSharp is always reported (it's the primary scope-driven language).
        output.Should().Contain("\"csharp\"");
        output.Should().Contain("NotStarted");
        output.Should().Contain("advice");
    }

    [Fact]
    public async Task Output_IsValidJson()
    {
        var tool = new LanguageServerStatusTool(NullToolContext.Instance);
        string output = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        // Round-trip parse to ensure deterministic JSON shape.
        using var doc = System.Text.Json.JsonDocument.Parse(output);
        doc.RootElement.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Object);
        doc.RootElement.TryGetProperty("csharp", out var csharp).Should().BeTrue();
        csharp.TryGetProperty("state", out _).Should().BeTrue();
        csharp.TryGetProperty("advice", out _).Should().BeTrue();
    }

    [Fact]
    public void Description_MentionsReadiness()
    {
        var tool = new LanguageServerStatusTool(NullToolContext.Instance);
        tool.Description.Should().Contain("readiness");
        tool.Description.Should().Contain("set_active_solution");
    }
}
