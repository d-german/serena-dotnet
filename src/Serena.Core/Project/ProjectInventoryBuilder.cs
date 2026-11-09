// Scans a project for the non-code files the symbol index cannot see and turns
// them into inventory rows: PackageReference, ProjectReference and selected
// properties from MSBuild files, and flattened keys from JSON configuration.
// Values that look like secrets are redacted before they reach the database,
// because an index can be bundled and shared.

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Serena.Lsp.Caching;

namespace Serena.Core.Project;

public static class ProjectInventoryBuilder
{
    public static readonly string[] IncludePatterns =
    [
        "**/*.csproj", "**/*.props", "**/*.targets",
        "**/appsettings*.json", "**/*settings*.json", "**/*.Octodeploy.json", "**/ocelot*.json",
        "**/launchSettings.json", "**/web.config",
    ];

    private static readonly string[] CapturedProperties =
    [
        "TargetFramework", "TargetFrameworks", "AssemblyName", "RootNamespace", "OutputType",
        "PackageId", "Version", "LangVersion", "Nullable", "IsPackable", "UserSecretsId",
    ];

    private static readonly Regex SecretKey = new(
        "password|pwd|secret|token|apikey|api_key|api-key|connectionstring|connection_string|credential|clientsecret|privatekey",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const int MaxValueLength = 120;
    public const long MaxFileBytes = 2 * 1024 * 1024;
    public const string Redacted = "<redacted>";

    /// <summary>
    /// Builds the inventory rows for a project, honouring its ignore rules.
    /// </summary>
    public static IReadOnlyList<InventoryEntry> Build(SerenaProject project)
    {
        var rows = new List<InventoryEntry>();
        // GatherSourceFiles returns project-relative, forward-slash paths.
        foreach (string relative in project.GatherSourceFiles(IncludePatterns))
        {
            string file = Path.GetFullPath(Path.Combine(project.Root, relative));
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > MaxFileBytes)
                {
                    continue;
                }
                string content = File.ReadAllText(file);
                rows.AddRange(Parse(file, content));
            }
            catch
            {
                // One unreadable file must not abort the inventory.
            }
        }
        return rows;
    }

    /// <summary>
    /// Parses one file by extension. Unknown extensions yield nothing.
    /// </summary>
    public static IReadOnlyList<InventoryEntry> Parse(string path, string content)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".csproj" or ".props" or ".targets" => ParseMsBuild(path, content),
            ".json" => ParseJson(path, content),
            ".config" => ParseXmlConfig(path, content),
            _ => [],
        };
    }

    public static IReadOnlyList<InventoryEntry> ParseMsBuild(string path, string content)
    {
        var rows = new List<InventoryEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (Exception)
        {
            return rows;
        }

        foreach (var element in doc.Descendants())
        {
            string name = element.Name.LocalName;
            switch (name)
            {
                case "PackageReference":
                case "PackageVersion":
                    {
                        string? id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
                        if (string.IsNullOrWhiteSpace(id))
                        {
                            break;
                        }
                        string? version = element.Attribute("Version")?.Value
                            ?? element.Attribute("VersionOverride")?.Value
                            ?? element.Element(element.Name.Namespace + "Version")?.Value;
                        rows.Add(new InventoryEntry(path, "package", id, Truncate(version)));
                        break;
                    }
                case "ProjectReference":
                    {
                        string? include = element.Attribute("Include")?.Value;
                        if (string.IsNullOrWhiteSpace(include))
                        {
                            break;
                        }
                        string referenced = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
                        rows.Add(new InventoryEntry(path, "project", referenced, Truncate(include)));
                        break;
                    }
                default:
                    if (CapturedProperties.Contains(name, StringComparer.OrdinalIgnoreCase)
                        && element.Parent?.Name.LocalName == "PropertyGroup")
                    {
                        rows.Add(new InventoryEntry(path, "property", name, Truncate(element.Value)));
                    }
                    break;
            }
        }
        return rows;
    }

    public static IReadOnlyList<InventoryEntry> ParseJson(string path, string content)
    {
        var rows = new List<InventoryEntry>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return rows;
        }

        using (doc)
        {
            Flatten(doc.RootElement, string.Empty, path, rows);
        }
        return rows;
    }

    private static void Flatten(JsonElement element, string prefix, string path, List<InventoryEntry> rows)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    string key = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
                    Flatten(property.Value, key, path, rows);
                }
                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, prefix + ":" + index++, path, rows);
                }
                break;
            default:
                if (prefix.Length == 0)
                {
                    return;
                }
                string? value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Null => null,
                    _ => element.GetRawText(),
                };
                rows.Add(new InventoryEntry(path, "json", prefix, Redact(prefix, value)));
                break;
        }
    }

    public static IReadOnlyList<InventoryEntry> ParseXmlConfig(string path, string content)
    {
        var rows = new List<InventoryEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (Exception)
        {
            return rows;
        }

        foreach (var add in doc.Descendants().Where(e => e.Name.LocalName == "add"))
        {
            string? key = add.Attribute("key")?.Value ?? add.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            string? value = add.Attribute("value")?.Value ?? add.Attribute("connectionString")?.Value;
            string section = add.Parent?.Name.LocalName ?? "add";
            rows.Add(new InventoryEntry(path, "json", section + ":" + key, Redact(key, value)));
        }
        return rows;
    }

    /// <summary>
    /// Replaces the value with a marker when the key names a secret, and
    /// truncates long values. Public so the policy is testable.
    /// </summary>
    public static string? Redact(string key, string? value)
    {
        if (value is null)
        {
            return null;
        }
        if (SecretKey.IsMatch(key))
        {
            return value.Length == 0 ? value : Redacted;
        }
        return Truncate(value);
    }

    private static string? Truncate(string? value)
    {
        if (value is null)
        {
            return null;
        }
        string single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxValueLength ? single : single[..MaxValueLength] + "…";
    }
}
