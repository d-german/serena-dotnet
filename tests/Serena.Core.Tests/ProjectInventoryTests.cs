// Inventory of MSBuild references and configuration keys: parsing, redaction, storage and query.

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Core.Project;
using Serena.Lsp.Caching;

namespace Serena.Core.Tests;

public sealed class ProjectInventoryTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectInventoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "serena_inv_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ParseMsBuild_CapturesPackagesProjectsAndProperties()
    {
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RootNamespace>Shared.Types</RootNamespace>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="CSharpFunctionalExtensions" Version="3.6.0" />
                <PackageReference Include="MediatR"><Version>12.3.0</Version></PackageReference>
                <ProjectReference Include="..\..\Shared\Shared.Types\Shared.Types.csproj" />
              </ItemGroup>
            </Project>
            """;

        var rows = ProjectInventoryBuilder.ParseMsBuild("src/A/A.csproj", csproj);

        rows.Should().Contain(r => r.Kind == "package" && r.Key == "CSharpFunctionalExtensions" && r.Value == "3.6.0");
        rows.Should().Contain(r => r.Kind == "package" && r.Key == "MediatR" && r.Value == "12.3.0");
        rows.Should().Contain(r => r.Kind == "project" && r.Key == "Shared.Types" && r.Value!.EndsWith("Shared.Types.csproj"));
        rows.Should().Contain(r => r.Kind == "property" && r.Key == "TargetFramework" && r.Value == "net10.0");
        rows.Should().Contain(r => r.Kind == "property" && r.Key == "RootNamespace" && r.Value == "Shared.Types");
    }

    [Fact]
    public void ParseJson_FlattensToColonPaths_IndexesArrays_AndRedactsSecrets()
    {
        const string json = """
            {
              // comment allowed
              "Thumbnail": { "Queue": { "Name": "thumbnails", "Hosts": ["a", "b"] } },
              "ConnectionStrings": { "Main": "Server=x;Password=hunter2" },
              "Auth": { "ClientSecret": "", "Enabled": true, "Retries": 3, "Nothing": null }
            }
            """;

        var rows = ProjectInventoryBuilder.ParseJson("appsettings.json", json);
        var byKey = rows.ToDictionary(r => r.Key, r => r.Value);

        byKey["Thumbnail:Queue:Name"].Should().Be("thumbnails");
        byKey["Thumbnail:Queue:Hosts:0"].Should().Be("a");
        byKey["Thumbnail:Queue:Hosts:1"].Should().Be("b");
        byKey["ConnectionStrings:Main"].Should().Be(ProjectInventoryBuilder.Redacted);
        byKey["Auth:ClientSecret"].Should().Be(string.Empty, "an empty secret is reported as empty, which is itself a finding");
        byKey["Auth:Enabled"].Should().Be("true");
        byKey["Auth:Retries"].Should().Be("3");
        byKey["Auth:Nothing"].Should().BeNull();
        rows.Should().OnlyContain(r => r.Kind == "json");
    }

    [Fact]
    public void ParseJson_Malformed_YieldsNothing()
    {
        ProjectInventoryBuilder.ParseJson("broken.json", "{ not json").Should().BeEmpty();
    }

    [Fact]
    public void Redact_TruncatesLongValues_AndMasksSecretKeys()
    {
        string longValue = new('x', ProjectInventoryBuilder.MaxValueLength + 20);
        ProjectInventoryBuilder.Redact("Plain", longValue)!.Length.Should().Be(ProjectInventoryBuilder.MaxValueLength + 1);
        ProjectInventoryBuilder.Redact("Api_Key", "abc").Should().Be(ProjectInventoryBuilder.Redacted);
        ProjectInventoryBuilder.Redact("Token", "abc").Should().Be(ProjectInventoryBuilder.Redacted);
        ProjectInventoryBuilder.Redact("Timeout", "30").Should().Be("30");
    }

    [Fact]
    public void Build_ScansProjectAndConfigFiles_SkipsBinObj_AndStoreAnswersQueries()
    {
        Write("src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="Serilog" Version="4.0.0" />
            </ItemGroup></Project>
            """);
        Write("src/App/appsettings.Production.json", """{ "Feature": { "Enabled": false } }""");
        Write("src/App/bin/Debug/appsettings.json", """{ "Feature": { "Enabled": true } }""");
        Write("src/App/Program.cs", "class P { }");

        var project = new SerenaProject(_tempDir, NullLogger<SerenaProject>.Instance);
        int rows = ProjectIndexer.BuildInventory(_tempDir, project, NullLoggerFactory.Instance);
        rows.Should().Be(2);

        var inventory = new ProjectInventory(Path.Combine(_tempDir, ".serena", "cache"), NullLogger.Instance);
        inventory.Exists.Should().BeTrue();

        var packages = inventory.Query("serilog", substring: true, kinds: ["package", "project"], pathPrefix: null, max: 10);
        packages.Should().ContainSingle(r => r.Key == "Serilog" && r.Value == "4.0.0");

        var config = inventory.Query("Feature:Enabled", substring: false, kinds: ["json"], pathPrefix: null, max: 10);
        config.Should().ContainSingle(r => r.Value == "false", "the bin copy is ignored");
        config[0].Path.Should().EndWith("appsettings.Production.json");

        inventory.Query("feature", substring: true, kinds: null, pathPrefix: Path.Combine(_tempDir, "elsewhere"), max: 10)
            .Should().BeEmpty();

        var stats = inventory.Stats();
        stats["package"].Should().Be(1);
        stats["json"].Should().Be(1);
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_tempDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}
