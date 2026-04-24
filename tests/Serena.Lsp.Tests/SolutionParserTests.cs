// Tests for Serena.Lsp.Project.SolutionParser - .sln/.slnx parsing via
// Microsoft.VisualStudio.SolutionPersistence. Uses temp-directory fixtures
// of valid solution + dummy csproj files so the parser sees them as resolvable.

using Serena.Lsp.Project;

namespace Serena.Lsp.Tests;

public class SolutionParserTests : IDisposable
{
    private readonly string _tempDir;

    public SolutionParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena-slntest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task GetCSharpProjectPathsAsync_ThrowsForMissingFile()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.sln");
        await Assert.ThrowsAsync<FileNotFoundException>(
            async () => await SolutionParser.GetCSharpProjectPathsAsync(missing));
    }

    [Fact]
    public async Task GetCSharpProjectPathsAsync_SlnxReturnsCSharpProjectsOnly()
    {
        // Create dummy csproj/vbproj files so resolver sees them
        WriteCsproj("AppA");
        WriteCsproj("AppB");
        WriteVbproj("VbLib");

        var slnxPath = Path.Combine(_tempDir, "Test.slnx");
        File.WriteAllText(slnxPath, """
            <Solution>
              <Project Path="AppA/AppA.csproj" />
              <Project Path="AppB/AppB.csproj" />
              <Project Path="VbLib/VbLib.vbproj" />
            </Solution>
            """);

        var projects = await SolutionParser.GetCSharpProjectPathsAsync(slnxPath);

        Assert.Equal(2, projects.Count);
        Assert.All(projects, p => Assert.EndsWith(".csproj", p, StringComparison.OrdinalIgnoreCase));
        Assert.All(projects, p => Assert.True(Path.IsPathRooted(p), $"Expected absolute path, got {p}"));
    }

    [Fact]
    public async Task GetCSharpProjectPathsAsync_MultipleSolutions_DedupesUnion()
    {
        WriteCsproj("Shared");
        WriteCsproj("OnlyA");
        WriteCsproj("OnlyB");

        var slnA = WriteSlnx("A.slnx",
            "<Project Path=\"Shared/Shared.csproj\" />",
            "<Project Path=\"OnlyA/OnlyA.csproj\" />");
        var slnB = WriteSlnx("B.slnx",
            "<Project Path=\"Shared/Shared.csproj\" />",
            "<Project Path=\"OnlyB/OnlyB.csproj\" />");

        var projects = await SolutionParser.GetCSharpProjectPathsAsync(new[] { slnA, slnB });

        Assert.Equal(3, projects.Count);
    }

    private void WriteCsproj(string name) => WriteProject(name, ".csproj");
    private void WriteVbproj(string name) => WriteProject(name, ".vbproj");

    private void WriteProject(string name, string ext)
    {
        var dir = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ext),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
    }

    private string WriteSlnx(string fileName, params string[] projectElements)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, "<Solution>\n  " + string.Join("\n  ", projectElements) + "\n</Solution>");
        return path;
    }
}
