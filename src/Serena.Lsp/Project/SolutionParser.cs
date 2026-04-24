// Serena.Lsp.Project.SolutionParser - parses .sln/.slnx and returns the absolute
// paths of contained C# projects (.csproj). Built on Microsoft's official
// Microsoft.VisualStudio.SolutionPersistence library (MIT) — the same library
// used by MSBuild, the .NET CLI, and C# Dev Kit, ensuring round-trip-correct
// behavior for both legacy .sln (text) and new .slnx (XML, VS 17.10+) formats.

using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace Serena.Lsp.Project;

/// <summary>
/// Parses Visual Studio solution files (.sln / .slnx) and returns the absolute
/// paths of the C# projects (.csproj) they reference.
/// </summary>
/// <remarks>
/// Solution folders are virtual constructs and are surfaced separately by the
/// underlying library; they are naturally excluded. Non-C# project types
/// (.vbproj, .fsproj, .vcxproj, .shproj, .pyproj, etc.) are filtered out by
/// extension since this parser feeds the C# Roslyn language server.
/// </remarks>
public static class SolutionParser
{
    private const string CSharpProjectExtension = ".csproj";

    /// <summary>
    /// Returns the absolute paths of every .csproj referenced by a solution file.
    /// Missing project files are silently skipped. Result is de-duplicated.
    /// </summary>
    /// <param name="solutionFilePath">Absolute or relative path to a .sln or .slnx file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>De-duplicated list of absolute .csproj paths. Empty if the file is
    /// not a recognized solution format or contains no C# projects.</returns>
    /// <exception cref="FileNotFoundException">The solution file does not exist.</exception>
    public static async Task<IReadOnlyList<string>> GetCSharpProjectPathsAsync(
        string solutionFilePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionFilePath);
        if (!File.Exists(solutionFilePath))
        {
            throw new FileNotFoundException("Solution file not found.", solutionFilePath);
        }

        var serializer = SolutionSerializers.GetSerializerByMoniker(solutionFilePath);
        if (serializer is null)
        {
            return Array.Empty<string>();
        }

        var fullSolutionPath = Path.GetFullPath(solutionFilePath);
        SolutionModel model = await serializer.OpenAsync(fullSolutionPath, cancellationToken).ConfigureAwait(false);
        var solutionDir = Path.GetDirectoryName(fullSolutionPath)!;

        return ExtractCSharpProjects(model, solutionDir);
    }

    /// <summary>
    /// Returns the union of .csproj paths referenced by multiple solution files,
    /// de-duplicated case-insensitively.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetCSharpProjectPathsAsync(
        IEnumerable<string> solutionFilePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solutionFilePaths);

        var result = new List<string>();
        foreach (var sln in solutionFilePaths)
        {
            var paths = await GetCSharpProjectPathsAsync(sln, cancellationToken).ConfigureAwait(false);
            result.AddRange(paths);
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractCSharpProjects(SolutionModel model, string solutionDir)
    {
        var projects = new List<string>();
        foreach (var project in model.SolutionProjects)
        {
            var resolved = ResolveProjectPath(project, solutionDir);
            if (resolved is not null)
            {
                projects.Add(resolved);
            }
        }
        return projects.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? ResolveProjectPath(SolutionProjectModel project, string solutionDir)
    {
        var relative = project.FilePath;
        if (string.IsNullOrEmpty(relative))
        {
            return null;
        }

        if (!relative.EndsWith(CSharpProjectExtension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(solutionDir, relative));
        return File.Exists(full) ? full : null;
    }
}
