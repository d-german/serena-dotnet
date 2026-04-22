// Language Server Base Infrastructure - Ported from solidlsp/language_servers/common.py
// Phase 4A: LanguageServerBase, RuntimeDependency, PlatformUtils

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol;

namespace Serena.Lsp.LanguageServers;

/// <summary>
/// Platform identifier for language server binary discovery.
/// </summary>
public enum PlatformId
{
    WindowsX64,
    WindowsArm64,
    LinuxX64,
    LinuxArm64,
    MacOsX64,
    MacOsArm64,
}

/// <summary>
/// Platform detection utilities.
/// </summary>
public static class PlatformUtils
{
    public static PlatformId GetPlatformId()
    {
        bool isArm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return isArm ? PlatformId.WindowsArm64 : PlatformId.WindowsX64;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return isArm ? PlatformId.LinuxArm64 : PlatformId.LinuxX64;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return isArm ? PlatformId.MacOsArm64 : PlatformId.MacOsX64;
        }

        throw new PlatformNotSupportedException("Unsupported operating system.");
    }

    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
}

/// <summary>
/// Represents a runtime dependency for a language server (binary, npm package, etc.).
/// Ported from common.py RuntimeDependency.
/// </summary>
public sealed record RuntimeDependency
{
    public required string Id { get; init; }
    public PlatformId? PlatformId { get; init; }
    public string? Url { get; init; }
    public string? Sha256 { get; init; }
    public IReadOnlyList<string>? AllowedHosts { get; init; }
    public string? ArchiveType { get; init; }
    public string? BinaryName { get; init; }
    public IReadOnlyList<string>? Command { get; init; }
    public string? PackageName { get; init; }
    public string? PackageVersion { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Abstract base class for language server implementations.
/// Each language server must provide its launch command, initialization options, and settings.
/// </summary>
public abstract class LanguageServerDefinition
{
    protected readonly ILogger Logger;

    protected LanguageServerDefinition(ILogger logger)
    {
        Logger = logger;
    }

    /// <summary>
    /// The language this server supports.
    /// </summary>
    public abstract Language Language { get; }

    /// <summary>
    /// Creates the ProcessLaunchInfo needed to start the language server.
    /// </summary>
    /// <param name="projectRoot">The root directory of the project.</param>
    /// <param name="settings">Custom language server settings.</param>
    public abstract ProcessLaunchInfo CreateLaunchInfo(string projectRoot, CustomLsSettings settings);

    /// <summary>
    /// Returns initialization options to send with the initialize request.
    /// </summary>
    public virtual object? GetInitializationOptions(string projectRoot, CustomLsSettings settings) => null;

    /// <summary>
    /// Returns settings to inject via workspace/didChangeConfiguration after initialization.
    /// </summary>
    public virtual object? GetWorkspaceSettings(string projectRoot, CustomLsSettings settings) => null;

    /// <summary>
    /// The runtime dependencies required by this language server.
    /// </summary>
    public virtual IReadOnlyList<RuntimeDependency> GetRuntimeDependencies() => [];

    /// <summary>
    /// Hook called after the language server has been initialized.
    /// Override to perform post-initialization setup (e.g., opening solutions/projects).
    /// </summary>
    public virtual Task PostStartAsync(LspClient client, string projectRoot, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Discovers the language server binary on the system.
    /// Returns the path if found, null otherwise.
    /// </summary>
    protected static string? FindInPath(string executableName)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] paths = pathVar.Split(Path.PathSeparator);

        string[] extensions = PlatformUtils.IsWindows
            ? [".exe", ".cmd", ".bat", ""]
            : [""];

        foreach (string dir in paths)
        {
            foreach (string ext in extensions)
            {
                string candidate = Path.Combine(dir, executableName + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Runs a command and returns its stdout output.
    /// </summary>
    protected static string RunCommand(string command, string arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(30));
        return output.Trim();
    }

    /// <summary>
    /// Returns the local resources directory for a language server, creating it if needed.
    /// Language servers are installed into ~/.serena-dotnet/language-servers/{serverName}/.
    /// </summary>
    protected static string GetLanguageServerResourcesDir(string serverName)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".serena-dotnet", "language-servers", serverName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Installs npm packages locally into the given resource directory.
    /// Invokes <c>node.exe npm-cli.js install --prefix ./ {packages}</c> directly,
    /// bypassing .cmd wrappers that hang in console-less subprocess environments.
    /// </summary>
    protected void TryLocalNpmInstall(string resourceDir, params string[] packages)
    {
        try
        {
            (string? node, string? npmCli) = FindNodeAndNpmCli();
            if (node is null || npmCli is null)
            {
                Logger.LogWarning(
                    "Node.js or npm not found. Install Node.js or run 'serena-dotnet doctor' to diagnose.");
                return;
            }

            Directory.CreateDirectory(resourceDir);
            string args = $"\"{npmCli}\" install --prefix ./ " + string.Join(' ', packages);
            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = args,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = resourceDir,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return;
            }

            process.StandardInput.Close();

            // Read stdout/stderr concurrently to prevent pipe buffer deadlock.
            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());
            process.WaitForExit(TimeSpan.FromSeconds(120));

            if (process.ExitCode == 0)
            {
                Logger.LogInformation("Successfully installed npm packages locally: {Packages}", string.Join(", ", packages));
            }
            else
            {
                Logger.LogWarning(
                    "Failed to install npm packages (exit code {ExitCode}): stderr={Error}, stdout={Output}",
                    process.ExitCode, stderrTask.Result, stdoutTask.Result);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Local npm install failed for: {Packages}", string.Join(", ", packages));
        }
    }

    /// <summary>
    /// Locates <c>node.exe</c> and the <c>npm-cli.js</c> script bundled alongside it.
    /// Returns nulls if either cannot be found.
    /// </summary>
    private static (string? nodeExe, string? npmCliJs) FindNodeAndNpmCli()
    {
        // On Windows, explicitly require the .exe to avoid .cmd/.bat wrappers.
        string? node = PlatformUtils.IsWindows
            ? FindInPathWithExtension("node", ".exe")
            : FindInPath("node");

        if (node is null)
        {
            return (null, null);
        }

        string npmCli = Path.Combine(
            Path.GetDirectoryName(node) ?? string.Empty,
            "node_modules", "npm", "bin", "npm-cli.js");

        return File.Exists(npmCli) ? (node, npmCli) : (null, null);
    }

    /// <summary>
    /// Searches PATH for an executable with an exact file extension.
    /// </summary>
    private static string? FindInPathWithExtension(string name, string extension)
    {
        string pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(dir, name + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds a locally installed npm binary by checking the node_modules/.bin directory.
    /// Returns the full absolute path if found, null otherwise.
    /// </summary>
    protected static string? FindLocalNpmBinary(string resourceDir, string binaryName)
    {
        string binDir = Path.Combine(resourceDir, "node_modules", ".bin");
        string[] extensions = PlatformUtils.IsWindows ? [".cmd", ".exe", ""] : [""];

        foreach (string ext in extensions)
        {
            string candidate = Path.Combine(binDir, binaryName + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Finds a .NET global tool by checking the well-known tools directory directly,
    /// avoiding any PATH manipulation.
    /// </summary>
    protected static string? FindDotnetToolPath(string toolName)
    {
        string toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");

        string[] extensions = PlatformUtils.IsWindows ? [".exe", ".cmd", ""] : [""];
        foreach (string ext in extensions)
        {
            string candidate = Path.Combine(toolsDir, toolName + ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}

/// <summary>
/// Registry of language server definitions, keyed by Language.
/// </summary>
public sealed class LanguageServerRegistry
{
    private readonly Dictionary<Language, LanguageServerDefinition> _definitions = [];

    public void Register(LanguageServerDefinition definition)
    {
        _definitions[definition.Language] = definition;
    }

    public LanguageServerDefinition? Get(Language language) =>
        _definitions.GetValueOrDefault(language);

    public bool Has(Language language) =>
        _definitions.ContainsKey(language);

    public IReadOnlyCollection<Language> RegisteredLanguages =>
        _definitions.Keys;
}
