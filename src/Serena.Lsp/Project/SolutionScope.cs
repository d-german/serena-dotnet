// Serena.Lsp.Project.SolutionScope - immutable description of which solution
// files (and therefore which projects) Roslyn should load. An empty scope means
// "no scope set; fall back to legacy recursive glob over the whole project root".

namespace Serena.Lsp.Project;

/// <summary>
/// Immutable description of the C# project scope provided to the Roslyn LSP.
/// An empty scope (<see cref="IsEmpty"/> = true) means no user-supplied scope —
/// callers should fall back to the legacy whole-repo recursive glob.
/// </summary>
/// <param name="SolutionPaths">Absolute paths to .sln/.slnx files that define
/// the working set of C# projects. May be empty.</param>
public sealed record SolutionScope(IReadOnlyList<string> SolutionPaths)
{
    /// <summary>A scope with no solutions — signals "use the default whole-repo glob".</summary>
    public static SolutionScope Empty { get; } = new(Array.Empty<string>());

    /// <summary>True when no solutions are in scope.</summary>
    public bool IsEmpty => SolutionPaths.Count == 0;

    /// <summary>
    /// Builds a scope from one or more solution file paths. Paths are normalized
    /// to absolute form and de-duplicated case-insensitively.
    /// </summary>
    public static SolutionScope FromSolutions(params string[] solutionPaths)
    {
        ArgumentNullException.ThrowIfNull(solutionPaths);
        if (solutionPaths.Length == 0)
        {
            return Empty;
        }

        var normalized = solutionPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return normalized.Length == 0 ? Empty : new SolutionScope(normalized);
    }

    /// <inheritdoc cref="FromSolutions(string[])"/>
    public static SolutionScope FromSolutions(IEnumerable<string> solutionPaths)
    {
        ArgumentNullException.ThrowIfNull(solutionPaths);
        return FromSolutions(solutionPaths.ToArray());
    }

    /// <summary>
    /// Aggregates every C# project (.csproj) referenced by every solution in the
    /// scope. Returns an empty list if the scope is empty.
    /// </summary>
    public Task<IReadOnlyList<string>> GetAllProjectsAsync(CancellationToken cancellationToken = default)
    {
        return IsEmpty
            ? Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>())
            : SolutionParser.GetCSharpProjectPathsAsync(SolutionPaths, cancellationToken);
    }
}
