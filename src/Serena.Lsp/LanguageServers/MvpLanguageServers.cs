// MVP Language Server Implementations - Phase 4B
// C#, Python, TypeScript, Rust, Go language server definitions

using Microsoft.Extensions.Logging;
using Serena.Lsp.Client;
using Serena.Lsp.Project;
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
        // Opt-in: only pass --clientProcessId when SERENA_LSP_PASS_CLIENT_PID=1
        // (some wrappers like SofusA/roslyn-language-server may not forward unknown args).
        bool passPid = Environment.GetEnvironmentVariable("SERENA_LSP_PASS_CLIENT_PID") == "1";
        string[] extraArgs = passPid
            ? [$"--clientProcessId={Environment.ProcessId}"]
            : [];
        // Polite mode is on by default; opt out via polite_mode: false
        bool politeMode = !string.Equals(settings.GetSetting("polite_mode"), "false", StringComparison.OrdinalIgnoreCase);
        if (explicitPath is not null)
        {
            return new ProcessLaunchInfo
            {
                Command = [explicitPath, "--stdio", .. extraArgs],
                WorkingDirectory = projectRoot,
                PoliteMode = politeMode,
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
                Command = [roslynPath, "--stdio", .. extraArgs],
                WorkingDirectory = projectRoot,
                PoliteMode = politeMode,
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
                PoliteMode = politeMode,
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

    /// <summary>
    /// Setting key carrying a semicolon-separated list of .sln/.slnx paths to scope Roslyn to.
    /// Populated by Serena.Core when activating a project that has <c>csharp.scope.solutions</c>
    /// set in its YAML config (or via the <c>set_active_solution</c> tool at runtime).
    /// </summary>
    public const string ScopeSolutionsSetting = "csharp_scope_solutions";

    /// <summary>
    /// Process-global env var that overrides the per-project YAML scope. Honored
    /// when <see cref="ScopeSolutionsSetting"/> is not present in <see cref="CustomLsSettings"/>.
    /// </summary>
    public const string ScopeSolutionsEnvVar = "SERENA_CSHARP_SOLUTIONS";

    public override async Task PostStartAsync(LspClient client, string projectRoot, CustomLsSettings settings, CancellationToken ct = default)
    {
        var scope = ResolveScope(settings);
        var sink = settings.ReadyStateSink;
        try
        {
            if (!scope.IsEmpty)
            {
                sink?.MarkLoading(Language.CSharp, scope.SolutionPaths.Count, $"{scope.SolutionPaths.Count} solution(s)");
                await OpenScopedSolutionsAsync(client, scope, ct);
            }
            else
            {
                sink?.MarkLoading(Language.CSharp, scopeDescription: "whole repo");
                await OpenAllInRepoAsync(client, projectRoot, ct);
            }

            await WaitForIndexingAsync(client, ct);
            sink?.MarkReady(Language.CSharp);
        }
        catch
        {
            sink?.MarkFailed(Language.CSharp);
            throw;
        }
    }

    private SolutionScope ResolveScope(CustomLsSettings settings)
    {
        // settings (from project YAML) wins over env, since it represents the
        // user's explicit per-project intent. Env var is the fallback for users
        // running Serena outside the SerenaProject path (e.g., bare CLI smoke tests).
        var fromSettings = ParseSolutionList(settings.GetSetting(ScopeSolutionsSetting));
        if (fromSettings is not null)
        {
            return fromSettings;
        }

        var fromEnv = ParseSolutionList(Environment.GetEnvironmentVariable(ScopeSolutionsEnvVar));
        return fromEnv ?? SolutionScope.Empty;
    }

    private static SolutionScope? ParseSolutionList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var paths = raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        return paths.Length == 0 ? null : SolutionScope.FromSolutions(paths);
    }

    private async Task OpenScopedSolutionsAsync(LspClient client, SolutionScope scope, CancellationToken ct)
    {
        Logger.LogInformation("C# scope active: {Count} solution(s) (skipping recursive csproj glob)",
            scope.SolutionPaths.Count);

        for (int i = 0; i < scope.SolutionPaths.Count; i++)
        {
            string slnPath = scope.SolutionPaths[i];
            if (!File.Exists(slnPath))
            {
                Logger.LogWarning("Scoped solution not found, skipping: {Path}", slnPath);
                continue;
            }
            string solutionUri = LspClient.PathToUri(slnPath);
            Logger.LogInformation("Opening scoped solution {Index}/{Total}: {Solution}",
                i + 1, scope.SolutionPaths.Count, Path.GetFileName(slnPath));
            await client.SendNotificationAsync("solution/open", new { solution = solutionUri });
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task OpenAllInRepoAsync(LspClient client, string projectRoot, CancellationToken ct)
    {
        // Discover all solution files (including subdirectories for mono-repos)
        var slnFiles = Directory.GetFiles(projectRoot, "*.sln", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(projectRoot, "*.slnx", SearchOption.AllDirectories))
            .ToArray();

        // Discover all project files
        var csprojFiles = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.AllDirectories);

        if (slnFiles.Length > 0)
        {
            Logger.LogInformation("Found {Count} solution file(s)", slnFiles.Length);
            for (int i = 0; i < slnFiles.Length; i++)
            {
                string solutionUri = LspClient.PathToUri(slnFiles[i]);
                Logger.LogInformation("Opening solution {Index}/{Total}: {Solution}",
                    i + 1, slnFiles.Length, Path.GetFileName(slnFiles[i]));
                await client.SendNotificationAsync("solution/open", new { solution = solutionUri });
            }
        }

        // Open all project files (Roslyn deduplicates by path)
        if (csprojFiles.Length > 0)
        {
            var projectUris = csprojFiles.Select(LspClient.PathToUri).ToArray();
            Logger.LogInformation("Opening {Count} project(s)", csprojFiles.Length);
            await client.SendNotificationAsync("project/open", new { projects = projectUris });
        }

        if (slnFiles.Length == 0 && csprojFiles.Length == 0)
        {
            Logger.LogInformation("No solution or project files found — skipping indexing wait");
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task WaitForIndexingAsync(LspClient client, CancellationToken ct)
    {
        // Wait for indexing: keep waiting as long as Roslyn is sending us activity ($/progress, diagnostics).
        // Give up after 30s of silence — works for any repo size.
        var inactivityTimeout = TimeSpan.FromSeconds(30);
        Logger.LogInformation("Waiting for project indexing (will keep waiting while Roslyn is active)\u2026");
        bool indexed = await client.WaitForProjectIndexingAsync(inactivityTimeout, ct);
        if (indexed)
        {
            Logger.LogInformation("C# project indexing completed");
        }
        else
        {
            Logger.LogWarning("C# project indexing stopped \u2014 no activity for {Timeout}s. Cross-file references may be incomplete",
                (int)inactivityTimeout.TotalSeconds);
        }
    }

    /// <summary>
    /// Returns "polite mode" workspace settings that disable Roslyn's
    /// background analyzer diagnostics. Agent sessions never consume diagnostics,
    /// so running them is pure waste — and on large solutions they dominate
    /// cold-load CPU and steady-state idle CPU. Compiler diagnostics on open
    /// files are kept (cheap, useful for parse-error visibility).
    ///
    /// Honors opt-out via <c>polite_mode: false</c> in CustomLsSettings.
    /// Schema verified against dotnet/vscode-csharp/package.json (the
    /// authoritative consumer of these keys). Delivered via
    /// <c>workspace/didChangeConfiguration</c> in <see cref="LspClient.StartAsync"/>.
    /// </summary>
    public override object? GetWorkspaceSettings(string projectRoot, CustomLsSettings settings)
    {
        if (string.Equals(settings.GetSetting("polite_mode"), "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new
        {
            dotnet = new
            {
                backgroundAnalysis = new
                {
                    analyzerDiagnosticsScope = "none",
                    compilerDiagnosticsScope = "openFiles",
                },
            },
        };
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
