// v1.0.26: SetActiveSolution large-scope warning tests. We exercise the
// FormatSuccess code path indirectly via reflection (it's private) since
// the public ApplyAsync requires a real project + solutions on disk.

using System.Reflection;
using FluentAssertions;
using Serena.Core.Tools;

namespace Serena.Core.Tests;

public class SetActiveSolutionLargeScopeWarningTests
{
    private static string InvokeFormatSuccess(int projectCount)
    {
        var method = typeof(SetActiveSolutionTool)
            .GetMethod("FormatSuccess", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("FormatSuccess not found");
        var result = method.Invoke(null,
        [
            "C:\\repo",
            new List<string> { "C:\\repo\\Sample.sln" },
            projectCount,
        ]);
        return result?.ToString() ?? "";
    }

    [Fact]
    public void FormatSuccess_ProjectCountAtThreshold_NoWarning()
    {
        string output = InvokeFormatSuccess(50);
        output.Should().NotContain("Large scope");
    }

    [Fact]
    public void FormatSuccess_ProjectCountAboveThreshold_PrependsWarning()
    {
        string output = InvokeFormatSuccess(51);
        output.Should().StartWith("\u26a0\ufe0f Large scope (51 projects)");
        output.Should().Contain("search_for_pattern");
        output.Should().Contain("get_language_server_status");
        output.Should().Contain("kill_language_server");
    }

    [Fact]
    public void FormatSuccess_VeryLargeScope_PrependsWarning()
    {
        string output = InvokeFormatSuccess(185);
        output.Should().Contain("Large scope (185 projects)");
    }
}
