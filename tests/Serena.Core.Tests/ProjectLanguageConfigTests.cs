using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Agent;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Lsp;
using Serena.Lsp.LanguageServers;

namespace Serena.Core.Tests;

public sealed class ProjectLanguageConfigTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectLanguageConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena-languages-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void ProjectConfig_ReadsPythonSerenaLanguagesField()
    {
        WriteProjectYml("""
            languages:
            - csharp
            - python
            """);

        var project = NewProject();

        project.GetConfiguredLanguages().Should().Equal(Language.CSharp, Language.Python);
        project.IsLanguageEnabled(Language.CSharp).Should().BeTrue();
        project.IsLanguageEnabled(Language.TypeScript).Should().BeFalse();
    }

    [Fact]
    public void ProjectIndexer_UsesConfiguredLanguages_WhenPresent()
    {
        WriteProjectYml("""
            languages:
            - csharp
            """);
        WriteFile("A.cs", "class A {}");
        WriteFile("script.py", "print('ignored')");
        WriteFile("app.ts", "export const ignored = true;");

        var project = NewProject();

        var grouped = ProjectIndexer.GroupFilesByLanguage(
            ["A.cs", "script.py", "app.ts"],
            project);

        grouped.Keys.Should().Equal(Language.CSharp);
        grouped[Language.CSharp].Should().Equal("A.cs");
    }

    [Fact]
    public void ProjectIndexer_KeepsLegacyAutoDetection_WhenLanguagesAreNotConfigured()
    {
        WriteFile("A.cs", "class A {}");
        WriteFile("script.py", "print('included')");

        var project = NewProject();

        var grouped = ProjectIndexer.GroupFilesByLanguage(
            ["A.cs", "script.py"],
            project);

        grouped.Keys.Should().BeEquivalentTo([Language.CSharp, Language.Python]);
    }

    [Fact]
    public void ProjectConfig_ReadsPerLanguageServerPathOverrides()
    {
        WriteProjectYml("""
            languages:
            - python
            ls_specific_settings:
              python:
                server_path: C:\tools\pyright-langserver.cmd
            """);

        var settings = NewProject().GetLanguageServerSettings(Language.Python);

        settings.Should().ContainKey("server_path");
        settings["server_path"].Should().Be("C:\\tools\\pyright-langserver.cmd");
        NewProject().GetLanguageServerSettings(Language.CSharp).Should().BeEmpty();
    }

    [Fact]
    public async Task Agent_DoesNotStartLanguageServerForDisabledLanguage()
    {
        WriteProjectYml("""
            languages:
            - csharp
            """);
        WriteFile("script.py", "print('disabled')");

        var agent = new SerenaAgent(
            new SerenaConfig(NullLogger<SerenaConfig>.Instance),
            new LanguageServerRegistry(),
            NullLoggerFactory.Instance);
        await agent.ActivateProjectAsync(_tempDir);

        var client = await agent.GetLanguageServerForFileAsync(Path.Combine(_tempDir, "script.py"));

        client.Should().BeNull();
    }

    private SerenaProject NewProject() =>
        new(_tempDir, NullLogger<SerenaProject>.Instance);

    private void WriteProjectYml(string body)
    {
        string serenaDir = Path.Combine(_tempDir, ".serena");
        Directory.CreateDirectory(serenaDir);
        File.WriteAllText(Path.Combine(serenaDir, "project.yml"), body);
    }

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }
}
