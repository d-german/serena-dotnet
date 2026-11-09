// The factual half of onboarding, written by the indexer instead of an agent:
// what the index and the inventory already know about a project, laid out as
// the markdown a reader wants first. Solutions, projects and their folders,
// frameworks, build and test commands, the rules the build enforces, the most
// depended-on projects, the shape of the code, packages and configuration.
// Every line is a count, a name, a path or a version; nothing is interpreted.
// Judgement (what the core domain is, which patterns the code follows, why)
// stays with the model-driven `onboarding` tool, which reads this first.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;
using Serena.Lsp.Protocol.Types;

namespace Serena.Core.Project;

public static class ProjectOverviewBuilder
{
    /// <summary>The memory the overview is written to; regenerated on every index, never edited by hand.</summary>
    public const string MemoryName = "generated/project-overview";

    /// <summary>Memories under this prefix are the indexer's, not an agent's, so they do not count as onboarding.</summary>
    public const string GeneratedPrefix = "generated/";

    private const int TopFolders = 15;
    private const int TopDependedOn = 8;
    private const int TopNamespaces = 12;
    private const int TopTypes = 10;
    private const int TopPackages = 30;
    private const int TopConfigSections = 12;
    private const int MaxEntryPoints = 10;
    private const int LineEndingSample = 300;

    private static readonly string[] TestPackages =
        ["xunit", "xunit.core", "xunit.v3", "NUnit", "MSTest.TestFramework", "Microsoft.NET.Test.Sdk", "TUnit"];

    private static readonly (string Category, string[] Prefixes)[] PackageCategories =
    [
        ("testing", ["xunit", "nunit", "mstest", "microsoft.net.test.sdk", "moq", "nsubstitute", "fluentassertions", "shouldly", "coverlet", "autofixture", "bogus", "tunit"]),
        ("web", ["microsoft.aspnetcore", "swashbuckle", "ocelot", "microsoft.openapi", "nswag"]),
        ("data", ["microsoft.entityframeworkcore", "dapper", "oracle.", "microsoft.data.", "npgsql", "mongodb", "stackexchange.redis", "system.data."]),
        ("messaging", ["mediatr", "masstransit", "rabbitmq", "azure.messaging", "confluent.kafka"]),
        ("resilience", ["polly"]),
        ("serialization", ["newtonsoft.json", "system.text.json", "protobuf", "messagepack", "yamldotnet"]),
        ("logging", ["serilog", "nlog", "microsoft.extensions.logging", "opentelemetry", "microsoft.applicationinsights"]),
        ("functional", ["csharpfunctionalextensions", "languageext", "oneof"]),
        ("security", ["microsoft.identitymodel", "system.identitymodel", "duende", "microsoft.aspnetcore.authentication"]),
    ];

    private static readonly Regex EditorConfigError = new(
        @"^\s*dotnet_diagnostic\.([A-Za-z]+\d+)\.severity\s*=\s*error\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Everything the overview is computed from, so the text can be tested without a project on disk.</summary>
    public sealed record Inputs(
        string ProjectRoot,
        string ProjectName,
        IReadOnlyList<string> RelativeFiles,
        IReadOnlyDictionary<Language, SymbolCache<UnifiedSymbolInformation[]>> Caches,
        IReadOnlyList<InventoryEntry> Inventory,
        string ToolVersion,
        DateTimeOffset Now)
    {
        /// <summary>Text of the root .editorconfig, when there is one.</summary>
        public string? EditorConfig { get; init; }

        /// <summary>Measured line-ending and BOM convention of the source files, when sampled.</summary>
        public string? FileConvention { get; init; }
    }

    /// <summary>
    /// Builds the overview from the project's files, its inventory database and the
    /// given symbol caches, saves it as <see cref="MemoryName"/> and returns the
    /// memory file path.
    /// </summary>
    public static string Write(
        SerenaProject project,
        IReadOnlyDictionary<Language, SymbolCache<UnifiedSymbolInformation[]>> caches,
        ILoggerFactory loggerFactory)
    {
        var inventory = new ProjectInventory(
            Path.Combine(project.Root, ".serena", "cache"),
            loggerFactory.CreateLogger<ProjectInventory>());
        var files = project.GatherSourceFiles();
        var inputs = new Inputs(
            project.Root, project.Name, files, caches, inventory.All(), ToolVersion(), DateTimeOffset.Now)
        {
            EditorConfig = ReadIfExists(Path.Combine(project.Root, ".editorconfig")),
            FileConvention = MeasureFileConvention(project.Root, files),
        };

        var memories = new MemoriesManager(project.SerenaDataFolder);
        memories.SaveMemory(MemoryName, Build(inputs));
        return memories.GetMemoryFilePath(MemoryName);
    }

    /// <summary>The overview as markdown.</summary>
    public static string Build(Inputs inputs)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(inputs.ProjectName).AppendLine();
        sb.AppendLine();
        sb.Append("Generated by serena-dotnet ").Append(inputs.ToolVersion)
          .Append(" from the symbol index and inventory on ")
          .Append(inputs.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
          .AppendLine(". Regenerated by every `project index` or `project overview`; do not edit. ")
          .AppendLine("Facts only: structure, build, dependencies, code shape, configuration. Design intent and conventions that live")
          .AppendLine("in people's heads belong in the other memories; see the `onboarding` tool.");
        sb.AppendLine();

        var projects = Projects(inputs);
        AppendSolution(sb, inputs, projects);
        AppendBuildAndTest(sb, inputs, projects);
        AppendDependedOn(sb, inputs, projects);
        AppendCodeShape(sb, inputs);
        AppendPackages(sb, inputs, projects);
        AppendConfiguration(sb, inputs);
        return sb.ToString();
    }

    // -- sections -------------------------------------------------------------

    private sealed record ProjectInfo(string RelativeCsproj, string Name, bool IsTest, IReadOnlyList<string> Packages);

    private static List<ProjectInfo> Projects(Inputs inputs)
    {
        var byCsproj = inputs.Inventory
            .Where(row => row.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.Path, StringComparer.OrdinalIgnoreCase);
        var projects = new List<ProjectInfo>();
        foreach (var group in byCsproj)
        {
            string relative = Relative(inputs.ProjectRoot, group.Key);
            string name = Path.GetFileNameWithoutExtension(relative);
            var packages = group.Where(row => row.Kind == "package").Select(row => row.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool isTest = packages.Any(p => TestPackages.Contains(p, StringComparer.OrdinalIgnoreCase))
                          || name.Contains("Test", StringComparison.OrdinalIgnoreCase);
            projects.Add(new ProjectInfo(relative, name, isTest, packages));
        }
        // Projects with no inventory rows at all (no packages, no references, no captured properties) still count.
        foreach (string file in inputs.RelativeFiles.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
        {
            string normalized = file.Replace('\\', '/');
            if (!projects.Any(p => p.RelativeCsproj.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                string name = Path.GetFileNameWithoutExtension(normalized);
                projects.Add(new ProjectInfo(normalized, name, name.Contains("Test", StringComparison.OrdinalIgnoreCase), []));
            }
        }
        return projects.OrderBy(p => p.RelativeCsproj, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AppendSolution(StringBuilder sb, Inputs inputs, List<ProjectInfo> projects)
    {
        sb.AppendLine("## Solution and projects");
        sb.AppendLine();
        var solutions = inputs.RelativeFiles
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (solutions.Count > 0)
        {
            sb.Append("Solutions: ").AppendLine(string.Join(", ", solutions.Select(s => $"`{s}`")));
        }

        int tests = projects.Count(p => p.IsTest);
        sb.Append("Projects: ").Append(projects.Count).Append(" (").Append(projects.Count - tests).Append(" source, ").Append(tests).AppendLine(" test).");

        var frameworks = PropertyValues(inputs, "TargetFramework").Concat(PropertyValues(inputs, "TargetFrameworks"))
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} ({g.Count()})").ToList();
        var langVersions = PropertyValues(inputs, "LangVersion").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nullable = PropertyValues(inputs, "Nullable").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var facts = new List<string>();
        if (frameworks.Count > 0)
        {
            facts.Add("target frameworks " + string.Join(", ", frameworks));
        }
        if (langVersions.Count > 0)
        {
            facts.Add("LangVersion " + string.Join(", ", langVersions));
        }
        if (nullable.Count > 0)
        {
            facts.Add("Nullable " + string.Join(", ", nullable));
        }
        if (inputs.RelativeFiles.Any(f => Path.GetFileName(f).Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)))
        {
            facts.Add("Directory.Build.props present");
        }
        if (facts.Count > 0)
        {
            sb.Append("Settings: ").Append(string.Join("; ", facts)).AppendLine(".");
        }
        sb.AppendLine();

        var folders = projects
            .GroupBy(p => ModuleFolder(p.RelativeCsproj), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count > 0)
        {
            sb.AppendLine("Project folders (projects, of which tests):");
            foreach (var folder in folders.Take(TopFolders))
            {
                sb.Append("- `").Append(folder.Key).Append("`: ").Append(folder.Count())
                  .Append(" (").Append(folder.Count(p => p.IsTest)).Append(" test)");
                var names = folder.Where(p => !p.IsTest).Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(6).ToList();
                if (names.Count > 0)
                {
                    sb.Append(": ").Append(string.Join(", ", names));
                    if (folder.Count(p => !p.IsTest) > names.Count)
                    {
                        sb.Append(", …");
                    }
                }
                sb.AppendLine();
            }
            if (folders.Count > TopFolders)
            {
                sb.Append("- … ").Append(folders.Count - TopFolders).AppendLine(" more folder(s)");
            }
            sb.AppendLine();
        }

        var entryPoints = inputs.RelativeFiles
            .Where(f => Path.GetFileName(f).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (entryPoints.Count > 0)
        {
            sb.Append("Entry points: ").Append(string.Join(", ", entryPoints.Take(MaxEntryPoints).Select(e => $"`{e}`")));
            if (entryPoints.Count > MaxEntryPoints)
            {
                sb.Append(" and ").Append(entryPoints.Count - MaxEntryPoints).Append(" more");
            }
            sb.AppendLine(".");
            sb.AppendLine();
        }
    }

    private static void AppendBuildAndTest(StringBuilder sb, Inputs inputs, List<ProjectInfo> projects)
    {
        sb.AppendLine("## Build and test");
        sb.AppendLine();
        string? solution = inputs.RelativeFiles
            .Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Replace('\\', '/'))
            .OrderBy(f => f.Count(c => c == '/')).ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        string target = solution ?? ".";
        sb.Append("Build: `dotnet build ").Append(target).AppendLine("`.");

        var testFrameworks = PackageVersions(inputs)
            .Where(p => TestPackages.Contains(p.Key, StringComparer.OrdinalIgnoreCase)
                        || p.Key.StartsWith("FluentAssertions", StringComparison.OrdinalIgnoreCase)
                        || p.Key is "Moq" or "NSubstitute" or "Shouldly" or "AutoFixture")
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key} {p.Value}")
            .ToList();
        sb.Append("Test: `dotnet test ").Append(target).Append("`");
        if (testFrameworks.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", testFrameworks)).Append(')');
        }
        int testProjects = projects.Count(p => p.IsTest);
        sb.Append("; ").Append(testProjects).AppendLine(" test project(s).");

        if (inputs.EditorConfig is { } editorConfig)
        {
            var errors = EditorConfigError.Matches(editorConfig).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (errors.Count > 0)
            {
                sb.Append("Rules the build enforces as errors (.editorconfig): ").Append(string.Join(", ", errors)).AppendLine(".");
            }
            var style = new List<string>();
            foreach (string key in new[] { "end_of_line", "charset", "indent_style", "indent_size" })
            {
                var match = Regex.Match(editorConfig, $@"^\s*{key}\s*=\s*([^\s#;]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    style.Add($"{key} = {match.Groups[1].Value}");
                }
            }
            if (style.Count > 0)
            {
                sb.Append("Declared style: ").Append(string.Join(", ", style)).AppendLine(".");
            }
        }
        if (inputs.FileConvention is { } convention)
        {
            sb.Append("Measured source files: ").Append(convention).AppendLine(".");
        }
        sb.AppendLine();
    }

    private static void AppendDependedOn(StringBuilder sb, Inputs inputs, List<ProjectInfo> projects)
    {
        var referenced = inputs.Inventory
            .Where(row => row.Kind == "project")
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, Count: g.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(TopDependedOn)
            .ToList();
        if (referenced.Count == 0)
        {
            return;
        }
        sb.AppendLine("## Most depended-on projects");
        sb.AppendLine();
        foreach (var (name, count) in referenced)
        {
            sb.Append("- ").Append(name).Append(": referenced by ").Append(count).AppendLine(count == 1 ? " project" : " projects");
        }
        sb.AppendLine();
    }

    private static void AppendCodeShape(StringBuilder sb, Inputs inputs)
    {
        foreach (var (language, cache) in inputs.Caches.OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal))
        {
            var keys = cache.Keys;
            if (keys.Count == 0)
            {
                continue;
            }
            var namespaces = new Dictionary<string, int>(StringComparer.Ordinal);
            var kinds = new Dictionary<SymbolKind, int>();
            var largest = new List<(string Name, int Members, string Path)>();
            foreach (string key in keys)
            {
                var outline = cache.TryGetUnchecked(key);
                if (outline is null)
                {
                    continue;
                }
                string relative = Relative(inputs.ProjectRoot, key);
                foreach (var symbol in outline)
                {
                    if (symbol.Kind == SymbolKind.Namespace)
                    {
                        string root = NamespaceRoot(symbol.Name);
                        namespaces[root] = namespaces.GetValueOrDefault(root) + 1;
                    }
                }
                foreach (var type in Types(outline))
                {
                    kinds[type.Kind] = kinds.GetValueOrDefault(type.Kind) + 1;
                    largest.Add((StripOverload(type.Name), type.Children.Count, relative));
                }
            }

            sb.Append("## Code shape (").Append(language.ToIdentifier()).AppendLine(")");
            sb.AppendLine();
            sb.Append(keys.Count).Append(" indexed files");
            if (kinds.Count > 0)
            {
                sb.Append(": ").Append(string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {KindName(k.Key)}")));
            }
            sb.AppendLine(".");
            if (namespaces.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Namespaces (files):");
                foreach (var ns in namespaces.OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.Ordinal).Take(TopNamespaces))
                {
                    sb.Append("- ").Append(ns.Key).Append(": ").Append(ns.Value).AppendLine();
                }
                if (namespaces.Count > TopNamespaces)
                {
                    sb.Append("- … ").Append(namespaces.Count - TopNamespaces).AppendLine(" more");
                }
            }
            if (largest.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Largest types (members):");
                foreach (var type in largest.OrderByDescending(t => t.Members).ThenBy(t => t.Name, StringComparer.Ordinal).Take(TopTypes))
                {
                    sb.Append("- ").Append(type.Name).Append(" (").Append(type.Members).Append("): `").Append(type.Path).AppendLine("`");
                }
            }
            sb.AppendLine();
        }
    }

    private static void AppendPackages(StringBuilder sb, Inputs inputs, List<ProjectInfo> projects)
    {
        var usage = inputs.Inventory
            .Where(row => row.Kind == "package")
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Id: g.Key,
                Projects: g.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                Versions: g.Select(r => r.Value).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(x => x.Projects).ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (usage.Count == 0)
        {
            return;
        }
        sb.AppendLine("## Packages");
        sb.AppendLine();
        sb.Append(usage.Count).AppendLine(" distinct package(s); the most used, by projects referencing them:");
        foreach (var category in usage.Take(TopPackages).GroupBy(p => Category(p.Id)).OrderBy(g => CategoryOrder(g.Key)))
        {
            sb.Append("- ").Append(category.Key).Append(": ")
              .AppendLine(string.Join(", ", category.Select(p => $"{p.Id}{VersionSuffix(p.Versions)} ({p.Projects})")));
        }
        if (usage.Count > TopPackages)
        {
            sb.Append("- … ").Append(usage.Count - TopPackages).AppendLine(" more; `symbols packages <name>` lists any of them");
        }
        sb.AppendLine();
    }

    private static void AppendConfiguration(StringBuilder sb, Inputs inputs)
    {
        var json = inputs.Inventory.Where(row => row.Kind == "json").ToList();
        if (json.Count == 0)
        {
            return;
        }
        int files = json.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var sections = json
            .GroupBy(r => r.Key.Split(':', 2)[0], StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        sb.AppendLine("## Configuration");
        sb.AppendLine();
        sb.Append(json.Count).Append(" key(s) in ").Append(files).AppendLine(" JSON configuration file(s); values that look like secrets are redacted in the index.");
        sb.Append("Top-level sections (keys): ")
          .Append(string.Join(", ", sections.Take(TopConfigSections).Select(s => $"{s.Key} ({s.Count()})")));
        if (sections.Count > TopConfigSections)
        {
            sb.Append(", …");
        }
        sb.AppendLine(".");
        sb.AppendLine("`symbols config <key>` finds any key; `find_occurrences` finds where a key name is used in code.");
        sb.AppendLine();
    }

    // -- helpers --------------------------------------------------------------

    private static IEnumerable<string> PropertyValues(Inputs inputs, string property) =>
        inputs.Inventory.Where(row => row.Kind == "property" && row.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(row.Value))
            .Select(row => row.Value!);

    private static IEnumerable<KeyValuePair<string, string>> PackageVersions(Inputs inputs) =>
        inputs.Inventory.Where(row => row.Kind == "package")
            .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new KeyValuePair<string, string>(g.Key, g.Select(r => r.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v)) ?? ""));

    private static string VersionSuffix(IReadOnlyList<string> versions) =>
        versions.Count == 0 ? "" : " " + string.Join("/", versions.Take(3));

    private static string Relative(string root, string path)
    {
        string normalized = path.Replace('\\', '/');
        string rootNormalized = root.Replace('\\', '/').TrimEnd('/') + "/";
        return normalized.StartsWith(rootNormalized, StringComparison.OrdinalIgnoreCase)
            ? normalized[rootNormalized.Length..]
            : normalized;
    }

    // "src/Modules/PatientAPI/Patient.API.Services/Patient.API.Services.csproj" → "src/Modules/PatientAPI";
    // a project right under a short path keeps its parent folder.
    private static string ModuleFolder(string relativeCsproj)
    {
        var parts = relativeCsproj.Replace('\\', '/').Split('/');
        var directories = parts[..^1];
        if (directories.Length == 0)
        {
            return ".";
        }
        int keep = Math.Min(directories.Length, directories.Length >= 3 ? 3 : directories.Length);
        return string.Join('/', directories[..keep]);
    }

    private static string NamespaceRoot(string name)
    {
        var parts = name.Split('.');
        return parts.Length <= 2 ? name : string.Join('.', parts[..2]);
    }

    private static IEnumerable<UnifiedSymbolInformation> Types(IEnumerable<UnifiedSymbolInformation> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is SymbolKind.Class or SymbolKind.Interface or SymbolKind.Struct or SymbolKind.Enum)
            {
                yield return symbol;
            }
            foreach (var nested in Types(symbol.Children))
            {
                yield return nested;
            }
        }
    }

    private static string KindName(SymbolKind kind) => kind switch
    {
        SymbolKind.Class => "classes",
        SymbolKind.Interface => "interfaces",
        SymbolKind.Struct => "structs",
        SymbolKind.Enum => "enums",
        _ => kind.ToString().ToLowerInvariant() + "s",
    };

    private static string StripOverload(string name)
    {
        int bracket = name.IndexOf('[');
        return bracket < 0 ? name : name[..bracket];
    }

    private static string Category(string packageId)
    {
        string lower = packageId.ToLowerInvariant();
        foreach (var (category, prefixes) in PackageCategories)
        {
            if (prefixes.Any(prefix => lower.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return category;
            }
        }
        return "other";
    }

    private static int CategoryOrder(string category)
    {
        int index = Array.FindIndex(PackageCategories, c => c.Category == category);
        return index < 0 ? PackageCategories.Length : index;
    }

    private static string? ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Samples source files for their actual line endings and BOM, since the declared
    /// style and the files often disagree and an agent must match the files.
    /// </summary>
    internal static string? MeasureFileConvention(string root, IReadOnlyList<string> relativeFiles)
    {
        var sample = relativeFiles
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
            .Take(LineEndingSample)
            .ToList();
        if (sample.Count == 0)
        {
            return null;
        }
        int crlf = 0, lf = 0, bom = 0, read = 0;
        foreach (string relative in sample)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(root, relative));
                read++;
                if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                {
                    bom++;
                }
                int pairs = 0, newlines = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (bytes[i] != (byte)'\n')
                    {
                        continue;
                    }
                    newlines++;
                    if (i > 0 && bytes[i - 1] == (byte)'\r')
                    {
                        pairs++;
                    }
                }
                if (newlines == 0)
                {
                    continue;
                }
                if (pairs * 2 >= newlines)
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
            catch
            {
                // Skip unreadable files; the sample is advisory.
            }
        }
        if (read == 0)
        {
            return null;
        }
        string endings = crlf >= lf ? $"CRLF in {crlf} of {crlf + lf}" : $"LF in {lf} of {crlf + lf}";
        return $"{endings} sampled files; UTF-8 BOM on {bom} of {read}";
    }

    private static string ToolVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        return version is null ? "dev" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
