// Serena.Lsp.Project.SolutionParser - parses .sln/.slnx and returns the absolute
// paths of contained C# projects (.csproj). Built on Microsoft's official
// Microsoft.VisualStudio.SolutionPersistence library (MIT) — the same library
// used by MSBuild, the .NET CLI, and C# Dev Kit, ensuring round-trip-correct
// behavior for both legacy .sln (text) and new .slnx (XML, VS 17.10+) formats.

using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;
using System.Xml;
using System.Xml.Linq;

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

    /// <summary>
    /// Returns the C# projects listed by the supplied solutions plus every
    /// resolvable transitive ProjectReference. This is used by focused cache
    /// indexing so a dependency outside both the application directory and
    /// the solution file is still available to cache-backed symbol queries.
    /// MSBuild-property and wildcard Includes cannot be resolved without a
    /// full MSBuild evaluation and are left for Roslyn.
    /// </summary>
    public static async Task<IReadOnlyList<string>> GetCSharpProjectClosureAsync(
        IEnumerable<string> solutionFilePaths,
        CancellationToken cancellationToken = default)
    {
        var directProjects = await GetCSharpProjectPathsAsync(
            solutionFilePaths, cancellationToken).ConfigureAwait(false);
        var seen = new HashSet<string>(directProjects, StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(directProjects);

        while (pending.TryDequeue(out string? projectPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string reference in await ReadProjectReferencesAsync(
                         projectPath, cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(reference))
                {
                    pending.Enqueue(reference);
                }
            }
        }

        return seen.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<IReadOnlyList<string>> ReadProjectReferencesAsync(
        string projectPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                projectPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 4096, useAsync: true);
            XDocument document = await XDocument.LoadAsync(
                stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            string projectDirectory = Path.GetDirectoryName(projectPath)!;
            var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (XElement element in document.Descendants()
                         .Where(e => e.Name.LocalName == "ProjectReference"))
            {
                string? include = element.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                foreach (string candidate in include.Split(
                             ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (candidate.Contains("$(", StringComparison.Ordinal)
                        || candidate.IndexOfAny(['*', '?']) >= 0)
                    {
                        continue;
                    }

                    string resolved = Path.GetFullPath(Path.Combine(projectDirectory, candidate));
                    if (resolved.EndsWith(CSharpProjectExtension, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(resolved))
                    {
                        references.Add(resolved);
                    }
                }
            }
            return references.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
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
