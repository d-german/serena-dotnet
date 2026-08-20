using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Project;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Core.Tests;

public sealed class ProjectFileDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectFileDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_discovery_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void GatherSourceFiles_SkipsGitIgnoredAndBuiltInGeneratedPaths()
    {
        WriteFile(".gitignore", "[Bb]in/\n[Oo]bj/\n");
        WriteFile("src/App.cs", "public class App {}");
        WriteFile("scripts/tool.py", "print('ok')");
        WriteFile("nested/.git/objects/51/b6c36015d512b230a14d809de68677cb0915c3", "git object");
        WriteFile("src/obj/Debug/net10.0/Generated.cs", "public class Generated {}");
        WriteFile("lib/bin/Debug/net10.0/Generated.cs", "public class Generated {}");
        WriteFile(".serena/cache/csharp/symbols.json", "{}");

        var project = NewProject();

        var files = project.GatherSourceFiles();

        files.Should().Contain("src/App.cs");
        files.Should().Contain("scripts/tool.py");
        files.Should().NotContain(f => f.Contains("/.git/", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(f => f.Contains("/obj/", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(f => f.Contains("/bin/", StringComparison.OrdinalIgnoreCase));
        files.Should().NotContain(f => f.StartsWith(".serena/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProjectIndexer_DoesNotTreatUnknownExtensionFilesAsCSharp()
    {
        WriteFile("src/App.cs", "public class App {}");
        WriteFile("nested/.git/objects/51/b6c36015d512b230a14d809de68677cb0915c3", "git object");
        WriteFile("README.md", "# Readme");

        var project = NewProject();

        var grouped = ProjectIndexer.GroupFilesByLanguage(
            ["src/App.cs", "nested/.git/objects/51/b6c36015d512b230a14d809de68677cb0915c3", "README.md"],
            project);

        grouped.Keys.Should().Equal(Language.CSharp);
        grouped[Language.CSharp].Should().Equal("src/App.cs");
    }

    [Fact]
    public async Task GatherSolutionSourceFiles_IndexesOnlySolutionProjectTreesAndLinkedSources()
    {
        WriteFile("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <Compile Include="../Shared/Linked.cs" Link="Linked.cs" />
                <ProjectReference Include="../Dependency/Dependency.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteFile("App/App.cs", "public class App {}");
        WriteFile("App/client.js", "export const app = true;");
        WriteFile("App/obj/Generated.cs", "public class Generated {}");
        WriteFile("Shared/Linked.cs", "public class Linked {}");
        WriteFile("Dependency/Dependency.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteFile("Dependency/Dependency.cs", "public class Dependency {}");
        WriteFile("Unrelated/Unrelated.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteFile("Unrelated/Unrelated.cs", "public class Unrelated {}");
        WriteFile("PatientWindow.slnx", """
            <Solution>
              <Project Path="App/App.csproj" />
            </Solution>
            """);

        var files = await ProjectIndexer.GatherSolutionSourceFilesAsync(
            NewProject(), _tempDir, [Path.Combine(_tempDir, "PatientWindow.slnx")]);

        files.Should().Contain("App/App.cs");
        files.Should().Contain("App/client.js");
        files.Should().Contain("Shared/Linked.cs");
        files.Should().Contain("Dependency/Dependency.cs");
        files.Should().NotContain("App/obj/Generated.cs");
        files.Should().NotContain("Unrelated/Unrelated.cs");
    }

    [Fact]
    public void PartitionFilesByCache_ReusesFreshEntriesAndReturnsOnlyChangedFiles()
    {
        WriteFile("src/Cached.cs", "public class Cached {}");
        WriteFile("src/Changed.cs", "public class Changed {}");

        string cacheDir = Path.Combine(_tempDir, ".serena", "cache", "csharp");
        var cache = new SymbolCache<UnifiedSymbolInformation[]>(
            cacheDir, "symbols.json", 1, NullLogger.Instance);
        string cachedPath = Path.Combine(_tempDir, "src/Cached.cs");
        string changedPath = Path.Combine(_tempDir, "src/Changed.cs");
        cache.Set(cachedPath, CacheFingerprint.ForFile(cachedPath), []);
        cache.Set(changedPath, CacheFingerprint.ForFile(changedPath), []);

        File.AppendAllText(changedPath, Environment.NewLine + "// changed");
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddSeconds(1));

        var (cachedFiles, filesToIndex) = ProjectIndexer.PartitionFilesByCache(
            ["src/Cached.cs", "src/Changed.cs"], _tempDir, cache);

        cachedFiles.Should().Equal("src/Cached.cs");
        filesToIndex.Should().Equal("src/Changed.cs");
    }

    private SerenaProject NewProject() =>
        new(_tempDir, NullLogger<SerenaProject>.Instance);

    private void WriteFile(string relativePath, string content)
    {
        string absolutePath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content);
    }
}
