using FluentAssertions;
using Serena.Core.Editor;
using Xunit;

namespace Serena.Core.Tests;

public class NamePathMatcherTests
{
    [Theory]
    [InlineData("myMethod", "MyClass/myMethod", true)]
    [InlineData("myMethod", "myMethod", true)]
    [InlineData("myMethod", "A/B/myMethod", true)]
    [InlineData("myMethod", "otherMethod", false)]
    public void SimpleName_MatchesLastSegment(string pattern, string namePath, bool expected)
    {
        var matcher = NamePathMatcher.Parse(pattern);
        matcher.Matches(namePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("MyClass/myMethod", "MyClass/myMethod", true)]
    [InlineData("MyClass/myMethod", "Namespace/MyClass/myMethod", true)]
    [InlineData("MyClass/myMethod", "OtherClass/myMethod", false)]
    public void RelativePath_MatchesSuffix(string pattern, string namePath, bool expected)
    {
        var matcher = NamePathMatcher.Parse(pattern);
        matcher.Matches(namePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("/MyClass/myMethod", "MyClass/myMethod", true)]
    [InlineData("/MyClass/myMethod", "Namespace/MyClass/myMethod", false)]
    public void AbsolutePath_MatchesExactly(string pattern, string namePath, bool expected)
    {
        var matcher = NamePathMatcher.Parse(pattern);
        matcher.Matches(namePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("myMethod[0]", "myMethod[0]", true)]
    [InlineData("myMethod[0]", "myMethod[1]", false)]
    [InlineData("myMethod[0]", "MyClass/myMethod[0]", true)]
    public void OverloadIndex_MatchesExact(string pattern, string namePath, bool expected)
    {
        var matcher = NamePathMatcher.Parse(pattern);
        matcher.Matches(namePath).Should().Be(expected);
    }

    [Theory]
    [InlineData("get", "getValue", true)]
    [InlineData("get", "getData", true)]
    [InlineData("get", "set", false)]
    [InlineData("MyClass/get", "MyClass/getValue", true)]
    public void SubstringMatching_MatchesPartial(string pattern, string namePath, bool expected)
    {
        var matcher = NamePathMatcher.Parse(pattern);
        matcher.Matches(namePath, substringMatching: true).Should().Be(expected);
    }
}
