// v1.0.34: GetSymbolsOverviewTool.IsOnlyContainers helper.

using System.Collections.Generic;
using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class GetSymbolsOverviewAutoDescendTests
{
    [Fact]
    public void IsOnlyContainers_OnlyNamespace_ReturnsTrue()
    {
        var overview = new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["Namespace"] = new() { new() { ["name_path"] = "Foo" } },
        };
        GetSymbolsOverviewTool.IsOnlyContainers(overview).Should().BeTrue();
    }

    [Fact]
    public void IsOnlyContainers_NamespaceAndModule_ReturnsTrue()
    {
        var overview = new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["Namespace"] = new(),
            ["Module"] = new(),
            ["Package"] = new(),
        };
        GetSymbolsOverviewTool.IsOnlyContainers(overview).Should().BeTrue();
    }

    [Fact]
    public void IsOnlyContainers_ContainsClass_ReturnsFalse()
    {
        var overview = new Dictionary<string, List<Dictionary<string, object?>>>
        {
            ["Namespace"] = new(),
            ["Class"] = new() { new() { ["name_path"] = "Foo/Bar" } },
        };
        GetSymbolsOverviewTool.IsOnlyContainers(overview).Should().BeFalse();
    }

    [Fact]
    public void IsOnlyContainers_Empty_ReturnsFalse()
    {
        var overview = new Dictionary<string, List<Dictionary<string, object?>>>();
        GetSymbolsOverviewTool.IsOnlyContainers(overview).Should().BeFalse();
    }
}
