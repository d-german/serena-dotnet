// v1.0.27: warm_language_server tool tests.

using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class WarmLanguageServerToolTests
{
    [Fact]
    public void Name_IsWarmLanguageServer()
    {
        var tool = new WarmLanguageServerTool(NullToolContext.Instance);
        tool.Name.Should().Be("warm_language_server");
    }

    [Fact]
    public void Description_MentionsBackgroundWarmupAndPolling()
    {
        var tool = new WarmLanguageServerTool(NullToolContext.Instance);
        tool.Description.Should().Contain("background");
        tool.Description.Should().Contain("get_language_server_status");
        tool.Description.Should().Contain("set_active_solution");
    }

    [Fact]
    public async Task Apply_NoActiveProject_ReturnsNotStartedSnapshotJson()
    {
        // NullToolContext has no active project so WarmLanguageServerAsync
        // short-circuits to a NotStarted snapshot. Verifies the tool serializes
        // and that the JSON shape is stable.
        var tool = new WarmLanguageServerTool(NullToolContext.Instance);
        string output = await tool.ExecuteAsync(new Dictionary<string, object?>(), CancellationToken.None);

        using var doc = System.Text.Json.JsonDocument.Parse(output);
        var root = doc.RootElement;
        root.GetProperty("language").GetString().Should().Be("csharp");
        root.GetProperty("state").GetString().Should().Be("NotStarted");
        root.TryGetProperty("advice", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Apply_UnknownLanguage_ReturnsErrorJson()
    {
        var tool = new WarmLanguageServerTool(NullToolContext.Instance);
        string output = await tool.ExecuteAsync(
            new Dictionary<string, object?> { ["language"] = "klingon" },
            CancellationToken.None);

        output.Should().Contain("error");
        output.Should().Contain("klingon");
    }
}
