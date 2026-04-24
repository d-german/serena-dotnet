// Tests for C# scope configuration: project.yml deserialization for
// csharp.scope.solutions, env-var override precedence, and runtime override.

using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Config;
using Serena.Core.Project;
using Serena.Lsp.Project;

namespace Serena.Core.Tests;

[CollectionDefinition("CSharpScopeEnvSerial", DisableParallelization = true)]
public class CSharpScopeEnvSerialCollection { }

[Collection("CSharpScopeEnvSerial")]
public class CSharpScopeConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalEnv;

    public CSharpScopeConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena-scope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _originalEnv = Environment.GetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar) ?? "";
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SerenaProject.CSharpSolutionsEnvVar,
            string.IsNullOrEmpty(_originalEnv) ? null : _originalEnv);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void GetCSharpScope_NoConfig_ReturnsEmpty()
    {
        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        Assert.True(project.GetCSharpScope().IsEmpty);
    }

    [Fact]
    public void GetCSharpScope_YamlSolutions_AreLoaded()
    {
        // Use YamlConfigLoader.Save to produce naming that matches the deserializer
        var serenaDir = Path.Combine(_tempDir, ".serena");
        Directory.CreateDirectory(serenaDir);
        var cfg = new ProjectConfig
        {
            Csharp = new CSharpProjectConfig
            {
                Scope = new CSharpScopeConfig
                {
                    Solutions = new List<string> { "sln/A.sln", "sln/B.slnx" }
                }
            }
        };
        var ymlPath = Path.Combine(serenaDir, "project.yml");
        YamlConfigLoader.Save(ymlPath, cfg);

        // Sanity check round-trip directly (bypasses SerenaProject's silent catch)
        var roundTrip = YamlConfigLoader.Load<ProjectConfig>(ymlPath);
        Assert.NotNull(roundTrip.Csharp);
        Assert.NotNull(roundTrip.Csharp!.Scope);
        Assert.Equal(2, roundTrip.Csharp.Scope!.Solutions!.Count);

        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        var scope = project.GetCSharpScope();

        Assert.False(scope.IsEmpty);
        Assert.Equal(2, scope.SolutionPaths.Count);
        Assert.All(scope.SolutionPaths, p => Assert.True(Path.IsPathRooted(p)));
    }

    [Fact]
    public void GetCSharpScope_EnvVar_OverridesYaml()
    {
        WriteProjectYml("""
            csharp:
              scope:
                solutions:
                  - yaml.sln
            """);

        var envPath1 = Path.Combine(_tempDir, "env1.sln");
        var envPath2 = Path.Combine(_tempDir, "env2.sln");
        Environment.SetEnvironmentVariable(
            SerenaProject.CSharpSolutionsEnvVar, $"{envPath1};{envPath2}");

        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        var scope = project.GetCSharpScope();

        Assert.Equal(2, scope.SolutionPaths.Count);
        Assert.DoesNotContain(scope.SolutionPaths, p => p.EndsWith("yaml.sln", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SetCSharpScope_RuntimeOverride_BeatsEnvAndYaml()
    {
        WriteProjectYml("""
            csharp:
              scope:
                solutions:
                  - yaml.sln
            """);

        Environment.SetEnvironmentVariable(
            SerenaProject.CSharpSolutionsEnvVar, Path.Combine(_tempDir, "env.sln"));

        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        var runtimePath = Path.Combine(_tempDir, "runtime.sln");
        project.SetCSharpScope(SolutionScope.FromSolutions(runtimePath));

        var scope = project.GetCSharpScope();
        Assert.Single(scope.SolutionPaths);
        Assert.EndsWith("runtime.sln", scope.SolutionPaths[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateAndPersistCSharpScope_WritesYamlAndSetsRuntime()
    {
        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        var slnPath = Path.Combine(_tempDir, "Persisted.sln");

        project.UpdateAndPersistCSharpScope(new[] { slnPath });

        // Runtime override active
        Assert.False(project.GetCSharpScope().IsEmpty);

        // YAML written
        var yml = File.ReadAllText(Path.Combine(_tempDir, ".serena", "project.yml"));
        Assert.Contains("solutions", yml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Persisted.sln", yml);
    }

    private void WriteProjectYml(string body)
    {
        var serenaDir = Path.Combine(_tempDir, ".serena");
        Directory.CreateDirectory(serenaDir);
        File.WriteAllText(Path.Combine(serenaDir, "project.yml"), body);
    }
}
