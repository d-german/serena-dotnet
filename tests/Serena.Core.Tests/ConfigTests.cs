// Config Tests - Phase 8

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Config;

namespace Serena.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void SerenaPaths_Instance_ReturnsSingleton()
    {
        var paths = SerenaPaths.Instance;
        paths.Should().NotBeNull();
        paths.SerenaUserHomeDir.Should().EndWith(".serena");
    }

    [Fact]
    public void SerenaConfig_RegisterAndGetProject()
    {
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        var project = new RegisteredProject { Name = "test", Path = @"C:\test\project" };

        config.RegisterProject(project);

        config.GetProject("test").Should().NotBeNull();
        config.GetProject("test")!.Path.Should().Be(@"C:\test\project");
        config.RegisteredProjects.Should().HaveCount(1);
    }

    [Fact]
    public void SerenaConfig_UnregisterProject()
    {
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        config.RegisterProject(new RegisteredProject { Name = "test", Path = @"C:\test" });

        config.UnregisterProject("test");

        config.RegisteredProjects.Should().BeEmpty();
    }

    [Fact]
    public void SerenaConfig_GetProject_ReturnsNull_WhenNotFound()
    {
        var config = new SerenaConfig(NullLogger<SerenaConfig>.Instance);
        config.GetProject("nonexistent").Should().BeNull();
    }

    [Fact]
    public void LoadFromFile_MissingFile_ReturnsDefaultConfig()
    {
        var logger = NullLogger<SerenaConfig>.Instance;
        var result = SerenaConfig.LoadFromFile(@"C:\nonexistent\config.yml", logger);

        result.IsSuccess.Should().BeTrue();
        result.Value.RegisteredProjects.Should().BeEmpty();
        result.Value.DefaultProject.Should().BeNull();
    }

    [Fact]
    public void LoadFromFile_ValidYaml_PopulatesProjects()
    {
        var logger = NullLogger<SerenaConfig>.Instance;
        string tempDir = Path.Combine(Path.GetTempPath(), $"serena-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            File.WriteAllText(configPath, """
                projects:
                  - name: alpha
                    path: /projects/alpha
                    description: Alpha project
                  - name: beta
                    path: /projects/beta
                default_project: alpha
                tool_timeout: 300
                default_max_tool_answer_chars: 200000
                """);

            var result = SerenaConfig.LoadFromFile(configPath, logger);

            result.IsSuccess.Should().BeTrue();
            var config = result.Value;
            config.RegisteredProjects.Should().HaveCount(2);
            config.RegisteredProjects["alpha"].Path.Should().Be("/projects/alpha");
            config.RegisteredProjects["alpha"].Description.Should().Be("Alpha project");
            config.RegisteredProjects["beta"].Path.Should().Be("/projects/beta");
            config.DefaultProject.Should().Be("alpha");
            config.ToolTimeout.Should().Be(300);
            config.DefaultMaxToolAnswerChars.Should().Be(200_000);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromFile_EmptyYaml_ReturnsEmptyConfig()
    {
        var logger = NullLogger<SerenaConfig>.Instance;
        string tempDir = Path.Combine(Path.GetTempPath(), $"serena-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            File.WriteAllText(configPath, "# Empty config\n");

            var result = SerenaConfig.LoadFromFile(configPath, logger);

            result.IsSuccess.Should().BeTrue();
            result.Value.RegisteredProjects.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromFile_SkipsEntriesWithMissingName()
    {
        var logger = NullLogger<SerenaConfig>.Instance;
        string tempDir = Path.Combine(Path.GetTempPath(), $"serena-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            File.WriteAllText(configPath, """
                projects:
                  - name: valid
                    path: /projects/valid
                  - path: /projects/no-name
                """);

            var result = SerenaConfig.LoadFromFile(configPath, logger);

            result.IsSuccess.Should().BeTrue();
            result.Value.RegisteredProjects.Should().HaveCount(1);
            result.Value.RegisteredProjects.Should().ContainKey("valid");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadFromFile_YamlRoundTrip()
    {
        var logger = NullLogger<SerenaConfig>.Instance;
        string tempDir = Path.Combine(Path.GetTempPath(), $"serena-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            // Save via YamlConfigLoader
            var model = new
            {
                projects = new[]
                {
                    new { name = "roundtrip", path = "/test/roundtrip", description = "RT test" }
                },
                default_project = "roundtrip"
            };
            YamlConfigLoader.Save(configPath, model);

            // Load via SerenaConfig
            var result = SerenaConfig.LoadFromFile(configPath, logger);

            result.IsSuccess.Should().BeTrue();
            result.Value.RegisteredProjects.Should().ContainKey("roundtrip");
            result.Value.RegisteredProjects["roundtrip"].Path.Should().Be("/test/roundtrip");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
