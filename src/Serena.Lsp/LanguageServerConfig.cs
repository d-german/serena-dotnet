// Language Server Configuration Types - Ported from solidlsp/ls_config.py
// Phase 3D: Language enum, FilenameMatcher, LanguageServerConfig

namespace Serena.Lsp;

/// <summary>
/// Enumeration of language servers supported by the LSP client.
/// Ported from solidlsp/ls_config.py Language enum.
/// </summary>
public enum Language
{
    CSharp,
    Python,
    Rust,
    Java,
    Kotlin,
    TypeScript,
    Go,
    Ruby,
    Dart,
    Cpp,
    CppCcls,
    Php,
    R,
    Perl,
    Clojure,
    Elixir,
    Elm,
    Terraform,
    Swift,
    Bash,
    Crystal,
    Zig,
    Lua,
    Luau,
    Nix,
    Erlang,
    OCaml,
    Al,
    FSharp,
    Rego,
    Scala,
    Julia,
    Fortran,
    Haskell,
    Haxe,
    Lean4,
    Groovy,
    Vue,
    PowerShell,
    Pascal,
    Matlab,
    Msl,
    // Experimental / alternative
    TypeScriptVts,
    PythonJedi,
    PythonTy,
    CSharpOmniSharp,
    RubySolargraph,
    PhpPhpactor,
    Markdown,
    Yaml,
    Toml,
    Hlsl,
    SystemVerilog,
    Solidity,
    Ansible,
}

/// <summary>
/// Extension methods for the Language enum.
/// </summary>
public static class LanguageExtensions
{
    private static readonly HashSet<Language> ExperimentalLanguages =
    [
        Language.Ansible,
        Language.TypeScriptVts,
        Language.PythonJedi,
        Language.PythonTy,
        Language.CSharpOmniSharp,
        Language.RubySolargraph,
        Language.PhpPhpactor,
        Language.Markdown,
        Language.Yaml,
        Language.Toml,
        Language.Groovy,
        Language.CppCcls,
        Language.Solidity,
    ];

    /// <summary>
    /// The string identifier used in YAML configuration and LSP communication.
    /// </summary>
    public static string ToIdentifier(this Language language) => language switch
    {
        Language.CSharp => "csharp",
        Language.Python => "python",
        Language.Rust => "rust",
        Language.Java => "java",
        Language.Kotlin => "kotlin",
        Language.TypeScript => "typescript",
        Language.Go => "go",
        Language.Ruby => "ruby",
        Language.Dart => "dart",
        Language.Cpp => "cpp",
        Language.CppCcls => "cpp_ccls",
        Language.Php => "php",
        Language.R => "r",
        Language.Perl => "perl",
        Language.Clojure => "clojure",
        Language.Elixir => "elixir",
        Language.Elm => "elm",
        Language.Terraform => "terraform",
        Language.Swift => "swift",
        Language.Bash => "bash",
        Language.Crystal => "crystal",
        Language.Zig => "zig",
        Language.Lua => "lua",
        Language.Luau => "luau",
        Language.Nix => "nix",
        Language.Erlang => "erlang",
        Language.OCaml => "ocaml",
        Language.Al => "al",
        Language.FSharp => "fsharp",
        Language.Rego => "rego",
        Language.Scala => "scala",
        Language.Julia => "julia",
        Language.Fortran => "fortran",
        Language.Haskell => "haskell",
        Language.Haxe => "haxe",
        Language.Lean4 => "lean4",
        Language.Groovy => "groovy",
        Language.Vue => "vue",
        Language.PowerShell => "powershell",
        Language.Pascal => "pascal",
        Language.Matlab => "matlab",
        Language.Msl => "msl",
        Language.TypeScriptVts => "typescript_vts",
        Language.PythonJedi => "python_jedi",
        Language.PythonTy => "python_ty",
        Language.CSharpOmniSharp => "csharp_omnisharp",
        Language.RubySolargraph => "ruby_solargraph",
        Language.PhpPhpactor => "php_phpactor",
        Language.Markdown => "markdown",
        Language.Yaml => "yaml",
        Language.Toml => "toml",
        Language.Hlsl => "hlsl",
        Language.SystemVerilog => "systemverilog",
        Language.Solidity => "solidity",
        Language.Ansible => "ansible",
        _ => language.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Parses a language identifier string into a Language enum value.
    /// </summary>
    public static Language? FromIdentifier(string identifier)
    {
        foreach (Language lang in Enum.GetValues<Language>())
        {
            if (string.Equals(lang.ToIdentifier(), identifier, StringComparison.OrdinalIgnoreCase))
            {
                return lang;
            }
        }
        return null;
    }

    /// <summary>
    /// Resolves a file extension (e.g. ".cs", ".py") to the best matching non-experimental Language.
    /// Returns null if no language matches.
    /// </summary>
    public static Language? FromFileExtension(string extension)
    {
        string pattern = "*" + (extension.StartsWith('.') ? extension : "." + extension);

        return Enum.GetValues<Language>()
            .Where(lang => !lang.IsExperimental())
            .OrderByDescending(lang => lang.GetPriority())
            .FirstOrDefault(lang => lang.GetSourceFilePatterns()
                .Any(p => string.Equals(p, pattern, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool IsExperimental(this Language language) =>
        ExperimentalLanguages.Contains(language);

    public static int GetPriority(this Language language)
    {
        if (language.IsExperimental())
        {
            return 0;
        }
        if (language == Language.Vue)
        {
            return 1;
        }
        return 2;
    }

    /// <summary>
    /// Returns file extension patterns for source files of this language.
    /// </summary>
    public static IReadOnlyList<string> GetSourceFilePatterns(this Language language) => language switch
    {
        Language.Python or Language.PythonJedi or Language.PythonTy => ["*.py", "*.pyi"],
        Language.Java => ["*.java"],
        Language.TypeScript or Language.TypeScriptVts =>
            ["*.ts", "*.tsx", "*.js", "*.jsx", "*.mts", "*.mtsx", "*.mjs", "*.mjsx", "*.cts", "*.ctsx", "*.cjs", "*.cjsx"],
        Language.CSharp or Language.CSharpOmniSharp => ["*.cs"],
        Language.Rust => ["*.rs"],
        Language.Go => ["*.go"],
        Language.Ruby => ["*.rb", "*.erb"],
        Language.RubySolargraph => ["*.rb"],
        Language.Cpp or Language.CppCcls => ["*.cpp", "*.h", "*.hpp", "*.c", "*.hxx", "*.cc", "*.cxx"],
        Language.Kotlin => ["*.kt", "*.kts"],
        Language.Dart => ["*.dart"],
        Language.Php or Language.PhpPhpactor => ["*.php"],
        Language.R => ["*.R", "*.r", "*.Rmd", "*.Rnw"],
        Language.Perl => ["*.pl", "*.pm", "*.t"],
        Language.Clojure => ["*.clj", "*.cljs", "*.cljc", "*.edn"],
        Language.Elixir => ["*.ex", "*.exs"],
        Language.Elm => ["*.elm"],
        Language.Terraform => ["*.tf", "*.tfvars", "*.tfstate"],
        Language.Swift => ["*.swift"],
        Language.Bash => ["*.sh", "*.bash"],
        Language.Crystal => ["*.cr"],
        Language.Zig => ["*.zig"],
        Language.Lua => ["*.lua"],
        Language.Luau => ["*.luau"],
        Language.Nix => ["*.nix"],
        Language.Erlang => ["*.erl", "*.hrl"],
        Language.OCaml => ["*.ml", "*.mli"],
        Language.Al => ["*.al"],
        Language.FSharp => ["*.fs", "*.fsi", "*.fsx"],
        Language.Rego => ["*.rego"],
        Language.Scala => ["*.scala", "*.sc"],
        Language.Julia => ["*.jl"],
        Language.Fortran => ["*.f90", "*.f95", "*.f03", "*.f08", "*.f", "*.for"],
        Language.Haskell => ["*.hs", "*.lhs"],
        Language.Haxe => ["*.hx"],
        Language.Lean4 => ["*.lean"],
        Language.Groovy => ["*.groovy", "*.gradle"],
        Language.Vue => ["*.vue"],
        Language.PowerShell => ["*.ps1", "*.psm1", "*.psd1"],
        Language.Pascal => ["*.pas", "*.pp", "*.lpr"],
        Language.Matlab => ["*.m"],
        Language.Msl => ["*.mrc"],
        Language.Markdown => ["*.md"],
        Language.Yaml => ["*.yml", "*.yaml"],
        Language.Toml => ["*.toml"],
        Language.Hlsl => ["*.hlsl", "*.hlsli", "*.fx", "*.fxh", "*.glsl", "*.vert", "*.frag", "*.wgsl"],
        Language.SystemVerilog => ["*.sv", "*.svh", "*.v", "*.vh"],
        Language.Solidity => ["*.sol"],
        Language.Ansible => ["*.yml", "*.yaml"],
        _ => [],
    };
}

/// <summary>
/// Matches filenames against a set of fnmatch-compatible patterns.
/// Ported from solidlsp/ls_config.py FilenameMatcher.
/// </summary>
public sealed class FilenameMatcher
{
    private readonly IReadOnlyList<string> _patterns;

    public FilenameMatcher(params string[] patterns)
    {
        _patterns = patterns;
    }

    public bool IsRelevantFilename(string filename)
    {
        string fn = Path.GetFileName(filename);
        foreach (string pattern in _patterns)
        {
            if (MatchesPattern(fn, pattern))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesPattern(string filename, string pattern)
    {
        // Simple fnmatch-style matching for *.ext patterns
        if (pattern.StartsWith('*'))
        {
            string extension = pattern[1..];
            return filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(filename, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Configuration for a specific language server.
/// Ported from solidlsp/ls_config.py LanguageServerConfig.
/// </summary>
public sealed record LanguageServerConfig
{
    public required Language Language { get; init; }
    public required FilenameMatcher SourceFileMatcher { get; init; }
    public string? ServerCommand { get; init; }
    public IReadOnlyList<string>? ServerArgs { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public string? InitializationOptionsJson { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Language-server-specific settings to inject via workspace/didChangeConfiguration.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Settings { get; init; }
}
