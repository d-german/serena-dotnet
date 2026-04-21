// Additional Language Server Implementations - Phase 10 (Batch 1)
// Java, Kotlin, C/C++, Ruby, PHP, Bash, Dart, Elixir

using Microsoft.Extensions.Logging;
using Serena.Lsp.Protocol;

namespace Serena.Lsp.LanguageServers;

public sealed class JavaLanguageServer : LanguageServerDefinition
{
    public JavaLanguageServer(ILogger<JavaLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Java;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("jdtls");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "jdtls", "-data", Path.Combine(projectRoot, ".jdtls-data")],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class KotlinLanguageServer : LanguageServerDefinition
{
    public KotlinLanguageServer(ILogger<KotlinLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Kotlin;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("kotlin-language-server");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "kotlin-language-server"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class CppLanguageServer : LanguageServerDefinition
{
    public CppLanguageServer(ILogger<CppLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Cpp;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("clangd");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "clangd", "--background-index"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class RubyLanguageServer : LanguageServerDefinition
{
    public RubyLanguageServer(ILogger<RubyLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Ruby;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("solargraph");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "solargraph", "stdio"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class PhpLanguageServer : LanguageServerDefinition
{
    public PhpLanguageServer(ILogger<PhpLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Php;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("phpactor");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "phpactor", "language-server"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class BashLanguageServer : LanguageServerDefinition
{
    public BashLanguageServer(ILogger<BashLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Bash;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("bash-language-server");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "bash-language-server", "start"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class DartLanguageServer : LanguageServerDefinition
{
    public DartLanguageServer(ILogger<DartLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Dart;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("dart");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "dart", "language-server", "--protocol=lsp"],
            WorkingDirectory = projectRoot,
        };
    }
}

public sealed class ElixirLanguageServer : LanguageServerDefinition
{
    public ElixirLanguageServer(ILogger<ElixirLanguageServer> logger) : base(logger) { }
    public override Language Language => Language.Elixir;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("elixir-ls");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "elixir-ls"],
            WorkingDirectory = projectRoot,
        };
    }
}

/// <summary>
/// Registers additional language servers beyond the MVP 5.
/// </summary>
public static class AdditionalLanguageServers
{
    public static void RegisterAll(LanguageServerRegistry registry, ILoggerFactory loggerFactory)
    {
        registry.Register(new JavaLanguageServer(loggerFactory.CreateLogger<JavaLanguageServer>()));
        registry.Register(new KotlinLanguageServer(loggerFactory.CreateLogger<KotlinLanguageServer>()));
        registry.Register(new CppLanguageServer(loggerFactory.CreateLogger<CppLanguageServer>()));
        registry.Register(new RubyLanguageServer(loggerFactory.CreateLogger<RubyLanguageServer>()));
        registry.Register(new PhpLanguageServer(loggerFactory.CreateLogger<PhpLanguageServer>()));
        registry.Register(new BashLanguageServer(loggerFactory.CreateLogger<BashLanguageServer>()));
        registry.Register(new DartLanguageServer(loggerFactory.CreateLogger<DartLanguageServer>()));
        registry.Register(new ElixirLanguageServer(loggerFactory.CreateLogger<ElixirLanguageServer>()));
    }
}
