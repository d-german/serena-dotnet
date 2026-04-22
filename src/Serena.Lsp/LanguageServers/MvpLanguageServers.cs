// MVP Language Server Implementations - Phase 4B
// C#, Python, TypeScript, Rust, Go language server definitions

using Microsoft.Extensions.Logging;
using Serena.Lsp.Protocol;
using SysProcess = System.Diagnostics.Process;
using SysProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace Serena.Lsp.LanguageServers;

/// <summary>
/// C# language server using the official Roslyn Language Server from Microsoft.
/// Auto-installs via <c>dotnet tool install -g roslyn-language-server --prerelease</c> if not found.
/// Falls back to csharp-ls if explicitly configured.
/// </summary>
public sealed class CSharpLanguageServer : LanguageServerDefinition
{
    public CSharpLanguageServer(ILogger<CSharpLanguageServer> logger) : base(logger) { }

    public override Language Language => Language.CSharp;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? explicitPath = settings.GetSetting("server_path");
        if (explicitPath is not null)
        {
            return new ProcessLaunchInfo
            {
                Command = [explicitPath, "--stdio"],
                WorkingDirectory = projectRoot,
            };
        }

        // Check well-known dotnet tools location first, then PATH
        string? roslynPath = FindDotnetToolPath("roslyn-language-server") ?? FindInPath("roslyn-language-server");
        if (roslynPath is null)
        {
            Logger.LogInformation("roslyn-language-server not found, attempting auto-install...");
            TryAutoInstall();
            roslynPath = FindDotnetToolPath("roslyn-language-server") ?? FindInPath("roslyn-language-server");
        }

        if (roslynPath is not null)
        {
            return new ProcessLaunchInfo
            {
                Command = [roslynPath, "--stdio"],
                WorkingDirectory = projectRoot,
            };
        }

        // Final fallback: try csharp-ls
        string? csharpLs = FindInPath("csharp-ls");
        if (csharpLs is not null)
        {
            Logger.LogWarning("roslyn-language-server not available, falling back to csharp-ls");
            return new ProcessLaunchInfo
            {
                Command = [csharpLs],
                WorkingDirectory = projectRoot,
            };
        }

        throw new InvalidOperationException(
            "No C# language server found. Install one with: dotnet tool install -g roslyn-language-server --prerelease");
    }

    private void TryAutoInstall()
    {
        try
        {
            var psi = new SysProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "tool install -g roslyn-language-server --prerelease",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = SysProcess.Start(psi);
            if (process is null)
            {
                return;
            }

            process.WaitForExit(TimeSpan.FromSeconds(60));
            if (process.ExitCode == 0)
            {
                Logger.LogInformation("Successfully installed roslyn-language-server");
            }
            else
            {
                string stderr = process.StandardError.ReadToEnd();
                Logger.LogWarning("Failed to auto-install roslyn-language-server: {Error}", stderr);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Auto-install of roslyn-language-server failed");
        }
    }
}

/// <summary>
/// Python language server via Pyright.
/// Auto-installs locally into ~/.serena-dotnet/language-servers/python/ if not found.
/// </summary>
public sealed class PythonLanguageServer : LanguageServerDefinition
{
    public PythonLanguageServer(ILogger<PythonLanguageServer> logger) : base(logger) { }

    public override Language Language => Language.Python;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? explicitPath = settings.GetSetting("server_path");
        if (explicitPath is not null)
        {
            return new ProcessLaunchInfo
            {
                Command = [explicitPath, "--stdio"],
                WorkingDirectory = projectRoot,
            };
        }

        string resourceDir = GetLanguageServerResourcesDir("python");
        string? path = FindLocalNpmBinary(resourceDir, "pyright-langserver");

        if (path is null)
        {
            Logger.LogInformation("pyright-langserver not found locally, attempting auto-install...");
            TryLocalNpmInstall(resourceDir, "pyright");
            path = FindLocalNpmBinary(resourceDir, "pyright-langserver");
        }

        // Fallback: check global PATH
        path ??= FindInPath("pyright-langserver");

        if (path is null)
        {
            throw new InvalidOperationException(
                "Pyright language server not found. Ensure Node.js/npm is installed, then restart the server to auto-install. "
                + "Run 'serena-dotnet doctor' to diagnose.");
        }

        return new ProcessLaunchInfo
        {
            Command = [path, "--stdio"],
            WorkingDirectory = projectRoot,
        };
    }

    public override object? GetWorkspaceSettings(string projectRoot, CustomLsSettings settings) =>
        new { python = new { analysis = new { autoSearchPaths = true, diagnosticMode = "openFilesOnly" } } };
}

/// <summary>
/// TypeScript/JavaScript language server via typescript-language-server.
/// Handles both .ts/.tsx and .js/.jsx files.
/// Auto-installs locally into ~/.serena-dotnet/language-servers/typescript/ if not found.
/// </summary>
public sealed class TypeScriptLanguageServer : LanguageServerDefinition
{
    public TypeScriptLanguageServer(ILogger<TypeScriptLanguageServer> logger) : base(logger) { }

    public override Language Language => Language.TypeScript;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? explicitPath = settings.GetSetting("server_path");
        if (explicitPath is not null)
        {
            return new ProcessLaunchInfo
            {
                Command = [explicitPath, "--stdio"],
                WorkingDirectory = projectRoot,
            };
        }

        string resourceDir = GetLanguageServerResourcesDir("typescript");
        string? path = FindLocalNpmBinary(resourceDir, "typescript-language-server");

        if (path is null)
        {
            Logger.LogInformation("typescript-language-server not found locally, attempting auto-install...");
            TryLocalNpmInstall(resourceDir, "typescript-language-server", "typescript");
            path = FindLocalNpmBinary(resourceDir, "typescript-language-server");
        }

        // Fallback: check global PATH
        path ??= FindInPath("typescript-language-server");

        if (path is null)
        {
            throw new InvalidOperationException(
                "TypeScript language server not found. Ensure Node.js/npm is installed, then restart the server to auto-install. "
                + "Run 'serena-dotnet doctor' to diagnose.");
        }

        return new ProcessLaunchInfo
        {
            Command = [path, "--stdio"],
            WorkingDirectory = projectRoot,
        };
    }
}

/// <summary>
/// Rust language server via rust-analyzer.
/// </summary>
public sealed class RustLanguageServer : LanguageServerDefinition
{
    public RustLanguageServer(ILogger<RustLanguageServer> logger) : base(logger) { }

    public override Language Language => Language.Rust;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("rust-analyzer");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "rust-analyzer"],
            WorkingDirectory = projectRoot,
        };
    }
}

/// <summary>
/// Go language server via gopls.
/// </summary>
public sealed class GoLanguageServer : LanguageServerDefinition
{
    public GoLanguageServer(ILogger<GoLanguageServer> logger) : base(logger) { }

    public override Language Language => Language.Go;

    public override ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings)
    {
        string? path = settings.GetSetting("server_path") ?? FindInPath("gopls");
        return new ProcessLaunchInfo
        {
            Command = [path ?? "gopls", "serve"],
            WorkingDirectory = projectRoot,
        };
    }
}

/// <summary>
/// Registers the built-in MVP language server definitions.
/// </summary>
public static class BuiltInLanguageServers
{
    public static void RegisterAll(LanguageServerRegistry registry, ILoggerFactory loggerFactory)
    {
        registry.Register(new CSharpLanguageServer(loggerFactory.CreateLogger<CSharpLanguageServer>()));
        registry.Register(new PythonLanguageServer(loggerFactory.CreateLogger<PythonLanguageServer>()));
        registry.Register(new TypeScriptLanguageServer(loggerFactory.CreateLogger<TypeScriptLanguageServer>()));
        registry.Register(new RustLanguageServer(loggerFactory.CreateLogger<RustLanguageServer>()));
        registry.Register(new GoLanguageServer(loggerFactory.CreateLogger<GoLanguageServer>()));
    }
}
