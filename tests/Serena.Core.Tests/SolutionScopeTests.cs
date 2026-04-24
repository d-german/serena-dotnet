// Tests for Serena.Lsp.Project.SolutionScope - immutable scope description
// with dedup, normalization, and Empty sentinel.

using Serena.Lsp.Project;

namespace Serena.Core.Tests;

public class SolutionScopeTests
{
    [Fact]
    public void Empty_HasNoSolutions()
    {
        Assert.True(SolutionScope.Empty.IsEmpty);
        Assert.Empty(SolutionScope.Empty.SolutionPaths);
    }

    [Fact]
    public void FromSolutions_NoArgs_ReturnsEmpty()
    {
        var scope = SolutionScope.FromSolutions();
        Assert.Same(SolutionScope.Empty, scope);
    }

    [Fact]
    public void FromSolutions_NormalizesAndDedupes()
    {
        var temp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var scope = SolutionScope.FromSolutions(
            Path.Combine(temp, "A.sln"),
            Path.Combine(temp, "a.sln"), // case-insensitive duplicate
            Path.Combine(temp, "B.sln"));

        Assert.False(scope.IsEmpty);
        Assert.Equal(2, scope.SolutionPaths.Count);
        Assert.All(scope.SolutionPaths, p => Assert.True(Path.IsPathRooted(p)));
    }

    [Fact]
    public void FromSolutions_OnlyWhitespace_ReturnsEmpty()
    {
        var scope = SolutionScope.FromSolutions("", "  ", null!);
        Assert.True(scope.IsEmpty);
    }

    [Fact]
    public async Task GetAllProjectsAsync_EmptyScope_ReturnsEmpty()
    {
        var projects = await SolutionScope.Empty.GetAllProjectsAsync();
        Assert.Empty(projects);
    }
}
