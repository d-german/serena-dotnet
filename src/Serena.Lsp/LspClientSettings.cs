// SolidLSP Settings - Ported from solidlsp/settings.py

using Microsoft.Extensions.Logging;

namespace Serena.Lsp;

/// <summary>
/// Global settings for the LSP client subsystem.
/// Ported from solidlsp/settings.py SolidLSPSettings.
/// </summary>
public sealed class LspClientSettings
{
    private readonly ILogger? _logger;

    /// <summary>
    /// Path to the directory for global LSP client data (not project-specific).
    /// Default: ~/.solidlsp
    /// </summary>
    public string LspDataDir { get; init; }

    /// <summary>
    /// Path to project-specific data directory for caches etc.
    /// </summary>
    public string ProjectDataPath { get; init; } = string.Empty;

    /// <summary>
    /// Language-server-specific settings keyed by Language enum.
    /// </summary>
    public IReadOnlyDictionary<Language, Dictionary<string, object>> LsSpecificSettings { get; init; } =
        new Dictionary<Language, Dictionary<string, object>>();

    public string LsResourcesDir => Path.Combine(LspDataDir, "language_servers", "static");

    public LspClientSettings(ILogger? logger = null)
    {
        _logger = logger;
        LspDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".solidlsp");
    }

    /// <summary>
    /// Ensures required directories exist.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(LspDataDir);
        Directory.CreateDirectory(LsResourcesDir);
    }

    /// <summary>
    /// Gets language-server-specific settings for the given language.
    /// </summary>
    public CustomLsSettings GetLsSpecificSettings(Language language)
    {
        var dict = LsSpecificSettings.TryGetValue(language, out var settings) ? settings : null;
        return new CustomLsSettings(dict, _logger);
    }
}

/// <summary>
/// Wraps a dictionary of custom language server settings with logging on access.
/// </summary>
public sealed class CustomLsSettings
{
    private readonly Dictionary<string, object>? _settings;
    private readonly ILogger? _logger;

    public CustomLsSettings(Dictionary<string, object>? settings = null, ILogger? logger = null)
    {
        _settings = settings ?? [];
        _logger = logger;
    }

    public T Get<T>(string key, T defaultValue)
    {
        if (_settings is not null && _settings.TryGetValue(key, out var value))
        {
            _logger?.LogInformation("Using custom LS setting {Value} for key '{Key}'", value, key);
            return (T)Convert.ChangeType(value, typeof(T));
        }
        return defaultValue;
    }

    public object? Get(string key, object? defaultValue = null) =>
        Get<object?>(key, defaultValue);

    /// <summary>
    /// Convenience: get a string setting, returns null if not found.
    /// </summary>
    public string? GetSetting(string key) =>
        Get<string?>(key, null);
}
