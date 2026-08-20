// v1.0.26: Static-text assertions on symbol tool descriptions and the
// initial-instructions cold-start guidance. Locks in the documentation
// surface that teaches the agent to use cache-first symbol queries and poll
// get_language_server_status before uncached semantic calls on large solutions.

using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class SymbolToolDescriptionTests
{
    private static IToolContext NullContext() => NullToolContext.Instance;

    [Fact]
    public void FindSymbolTool_DescriptionMentionsWarmupGuidance()
    {
        var tool = new FindSymbolTool(NullContext());
        tool.Description.Should().Contain("cache-first");
        tool.Description.Should().Contain("get_language_server_status");
    }

    [Fact]
    public void FindReferencingSymbolsTool_DescriptionMentionsWarmupGuidance()
    {
        var tool = new FindReferencingSymbolsTool(NullContext());
        tool.Description.Should().Contain("cached symbol tools");
        tool.Description.Should().Contain("get_language_server_status");
    }

    [Fact]
    public void GetSymbolsOverviewTool_DescriptionMentionsWarmupGuidance()
    {
        var tool = new GetSymbolsOverviewTool(NullContext());
        tool.Description.Should().Contain("cache-first");
        tool.Description.Should().Contain("get_language_server_status");
    }
}
