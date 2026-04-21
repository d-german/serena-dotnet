// YAML Configuration Loader - Ported from serena/util/yaml.py and config loading logic
// Handles reading/writing YAML configuration files for Serena.

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Serena.Core.Config;

/// <summary>
/// YAML configuration file utilities.
/// </summary>
public static class YamlConfigLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .DisableAliases()
        .Build();

    /// <summary>
    /// Loads a YAML file and deserializes it to the specified type.
    /// </summary>
    public static T Load<T>(string filePath)
    {
        string yaml = File.ReadAllText(filePath);
        return Deserializer.Deserialize<T>(yaml);
    }

    /// <summary>
    /// Tries to load a YAML file. Returns null if the file doesn't exist.
    /// </summary>
    public static T? TryLoad<T>(string filePath) where T : class
    {
        if (!File.Exists(filePath))
        {
            return null;
        }
        return Load<T>(filePath);
    }

    /// <summary>
    /// Serializes an object and saves it to a YAML file.
    /// </summary>
    public static void Save<T>(string filePath, T data)
    {
        string directory = Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        string yaml = Serializer.Serialize(data);
        File.WriteAllText(filePath, yaml);
    }

    /// <summary>
    /// Loads a YAML file as a raw dictionary for flexible access.
    /// </summary>
    public static Dictionary<string, object>? LoadAsDictionary(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }
        string yaml = File.ReadAllText(filePath);
        return Deserializer.Deserialize<Dictionary<string, object>>(yaml);
    }
}
