// Language Server Base Infrastructure - Ported from solidlsp/language_servers/common.py
// Phase 4A: LanguageServerBase, RuntimeDependency, PlatformUtils

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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
    /// Attempts to install npm packages globally.
    /// </summary>
    protected void TryNpmInstall(params string[] packages)
    {
        try
        {
            string npm = PlatformUtils.IsWindows ? "npm.cmd" : "npm";
            string args = "install -g " + string.Join(' ', packages);
            var psi = new ProcessStartInfo
            {
                FileName = npm,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
            {
                return;
            }

            process.WaitForExit(TimeSpan.FromSeconds(120));
            if (process.ExitCode == 0)
            {
                Logger.LogInformation("Successfully installed npm packages: {Packages}", string.Join(", ", packages));
            }
            else
            {
                string stderr = process.StandardError.ReadToEnd();
                Logger.LogWarning("Failed to install npm packages: {Error}", stderr);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "npm install failed for: {Packages}", string.Join(", ", packages));
        }
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
