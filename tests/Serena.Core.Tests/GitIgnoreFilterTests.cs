using FluentAssertions;
using Serena.Core.Project;
using Xunit;

namespace Serena.Core.Tests;

public class GitIgnoreFilterTests
{
    [Fact]
    public void BuiltInIgnores_AlwaysApply()
    {
        var filter = GitIgnoreFilter.FromContent("");
        filter.IsIgnored(".git").Should().BeTrue();
        filter.IsIgnored(".git/config").Should().BeTrue();
        filter.IsIgnored(".serena").Should().BeTrue();
        filter.IsIgnored(".serena/memories/test.md").Should().BeTrue();
    }

    [Fact]
    public void SimpleFilePattern_MatchesAnywhere()
    {
        var filter = GitIgnoreFilter.FromContent("*.dll");
        filter.IsIgnored("MyLib.dll").Should().BeTrue();
        filter.IsIgnored("bin/Debug/MyLib.dll").Should().BeTrue();
        filter.IsIgnored("src/MyLib.cs").Should().BeFalse();
    }

    [Fact]
    public void DirectoryPattern_MatchesDirectoryAndContents()
    {
        var filter = GitIgnoreFilter.FromContent("bin/\nobj/\nnode_modules/");
        filter.IsIgnored("bin").Should().BeTrue();
        filter.IsIgnored("bin/Debug/net8.0").Should().BeTrue();
        filter.IsIgnored("obj/project.assets.json").Should().BeTrue();
        filter.IsIgnored("node_modules/lodash/index.js").Should().BeTrue();
        filter.IsIgnored("src/bin.cs").Should().BeFalse();
    }

    [Fact]
    public void NegationPattern_UnIgnoresFile()
    {
        var filter = GitIgnoreFilter.FromContent("*.log\n!important.log");
        filter.IsIgnored("debug.log").Should().BeTrue();
        filter.IsIgnored("important.log").Should().BeFalse();
    }

    [Fact]
    public void RootedPattern_OnlyMatchesAtRoot()
    {
        var filter = GitIgnoreFilter.FromContent("/build");
        filter.IsIgnored("build").Should().BeTrue();
        filter.IsIgnored("build/output.dll").Should().BeTrue();
        filter.IsIgnored("src/build").Should().BeFalse();
    }

    [Fact]
    public void DoubleStarPattern_MatchesAcrossDirectories()
    {
        var filter = GitIgnoreFilter.FromContent("**/logs");
        filter.IsIgnored("logs").Should().BeTrue();
        filter.IsIgnored("src/logs").Should().BeTrue();
        filter.IsIgnored("a/b/c/logs").Should().BeTrue();
    }

    [Fact]
    public void CommentsAndEmptyLines_AreSkipped()
    {
        var filter = GitIgnoreFilter.FromContent("# This is a comment\n\n*.tmp\n# Another comment");
        filter.IsIgnored("test.tmp").Should().BeTrue();
        filter.IsIgnored("test.cs").Should().BeFalse();
    }

    [Fact]
    public void EmptyPath_IsNotIgnored()
    {
        var filter = GitIgnoreFilter.FromContent("*.dll");
        filter.IsIgnored("").Should().BeFalse();
    }

    [Fact]
    public void PathWithSlash_IsRelativeToBase()
    {
        var filter = GitIgnoreFilter.FromContent("docs/internal");
        filter.IsIgnored("docs/internal").Should().BeTrue();
        filter.IsIgnored("docs/internal/secret.md").Should().BeTrue();
        filter.IsIgnored("src/docs/internal").Should().BeFalse();
    }
}
