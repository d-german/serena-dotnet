// Prompt Template System - Ported from interprompt/
// Phase 7D: PromptTemplate, PromptList, PromptCollection, PromptFactory

using Fluid;
using Fluid.Values;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Serena.Core.Templates;

/// <summary>
/// A single prompt template that can be rendered with parameters.
/// Ported from interprompt/multilang_prompt.py PromptTemplate.
/// Uses Fluid (Liquid-compatible) as the template engine instead of Jinja2.
/// </summary>
public sealed class PromptTemplate
{
    private readonly IFluidTemplate _template;

    public string Name { get; }

    public PromptTemplate(string name, string templateString)
    {
        Name = name;
        var parser = new FluidParser();
        if (!parser.TryParse(templateString.Trim(), out var template, out string error))
        {
            throw new ArgumentException($"Failed to parse template '{name}': {error}");
        }
        _template = template;
    }

    public string Render(IDictionary<string, object?> parameters)
    {
        var context = new TemplateContext();
        foreach (var (key, value) in parameters)
        {
            context.SetValue(key, FluidValue.Create(value, context.Options));
        }
        return _template.Render(context);
    }
}

/// <summary>
/// A list of prompt strings.
/// Ported from interprompt/multilang_prompt.py PromptList.
/// </summary>
public sealed class PromptList
{
    public IReadOnlyList<string> Items { get; }

    public PromptList(IEnumerable<string> items)
    {
        Items = items.Select(x => x.Trim()).ToList();
    }

    public string ToFormattedString()
    {
        const string bullet = " * ";
        string indent = new(' ', bullet.Length);
        var formatted = Items.Select(x => x.Replace("\n", "\n" + indent));
        return string.Join("\n * ", formatted);
    }
}

/// <summary>
/// A collection of prompt templates and prompt lists loaded from YAML files.
/// Ported from interprompt/multilang_prompt.py MultiLangPromptCollection (single-language subset).
/// </summary>
public sealed class PromptCollection
{
    private readonly Dictionary<string, PromptTemplate> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PromptList> _lists = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public PromptCollection(IEnumerable<string> promptDirectories, ILogger? logger = null)
    {
        _logger = logger;
        bool isFirst = true;
        foreach (string dir in promptDirectories)
        {
            LoadFromDirectory(dir, isFirst);
            isFirst = false;
        }
    }

    public PromptCollection(string promptDirectory, ILogger? logger = null)
        : this([promptDirectory], logger)
    {
    }

    private void LoadFromDirectory(string directory, bool isFirst)
    {
        if (!Directory.Exists(directory))
        {
            _logger?.LogDebug("Prompt directory does not exist: {Directory}", directory);
            return;
        }

        foreach (string file in Directory.EnumerateFiles(directory, "*.yml")
                     .Concat(Directory.EnumerateFiles(directory, "*.yaml")))
        {
            string yaml = File.ReadAllText(file);
            var data = YamlDeserializer.Deserialize<Dictionary<string, object>>(yaml);

            if (!data.TryGetValue("prompts", out var promptsObj) || promptsObj is not Dictionary<object, object> prompts)
            {
                _logger?.LogWarning("Invalid YAML structure in {File}: missing 'prompts' key", file);
                continue;
            }

            foreach (var (nameObj, valueObj) in prompts)
            {
                string name = nameObj.ToString()!;

                if (valueObj is List<object> list)
                {
                    if (!isFirst && _lists.ContainsKey(name))
                    {
                        _logger?.LogDebug("Skipping list '{Name}' (already exists)", name);
                        continue;
                    }
                    _lists[name] = new PromptList(list.Select(x => x.ToString()!));
                }
                else if (valueObj is string templateStr)
                {
                    if (!isFirst && _templates.ContainsKey(name))
                    {
                        _logger?.LogDebug("Skipping template '{Name}' (already exists)", name);
                        continue;
                    }
                    _templates[name] = new PromptTemplate(name, templateStr);
                }
                else
                {
                    _logger?.LogWarning("Invalid prompt type for '{Name}' in {File}", name, file);
                }
            }
        }
    }

    public IReadOnlyList<string> GetTemplateNames() => _templates.Keys.ToList();
    public IReadOnlyList<string> GetListNames() => _lists.Keys.ToList();

    public PromptTemplate GetTemplate(string name) =>
        _templates.TryGetValue(name, out var template)
            ? template
            : throw new KeyNotFoundException($"Prompt template '{name}' not found");

    public PromptList GetList(string name) =>
        _lists.TryGetValue(name, out var list)
            ? list
            : throw new KeyNotFoundException($"Prompt list '{name}' not found");

    public string RenderTemplate(string name, IDictionary<string, object?> parameters) =>
        GetTemplate(name).Render(parameters);

    public bool HasTemplate(string name) => _templates.ContainsKey(name);
    public bool HasList(string name) => _lists.ContainsKey(name);
}

/// <summary>
/// Factory for creating and rendering prompt templates.
/// Ported from interprompt/prompt_factory.py PromptFactoryBase.
/// </summary>
public class PromptFactory
{
    private readonly PromptCollection _collection;

    public PromptFactory(PromptCollection collection)
    {
        _collection = collection;
    }

    public PromptFactory(IEnumerable<string> promptDirectories, ILogger? logger = null)
        : this(new PromptCollection(promptDirectories, logger))
    {
    }

    public string RenderPrompt(string promptName, IDictionary<string, object?> parameters) =>
        _collection.RenderTemplate(promptName, parameters);

    public PromptList GetPromptList(string listName) =>
        _collection.GetList(listName);

    public bool HasPrompt(string name) => _collection.HasTemplate(name);
    public bool HasList(string name) => _collection.HasList(name);
}
