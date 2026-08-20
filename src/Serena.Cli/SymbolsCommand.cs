// Serena.Cli - read-only symbol index queries
//
// These commands answer entirely from the on-disk symbol index written by
// `serena project index` (.serena/cache/<language>/symbols.db). No language
// server is started, which matters on a large multi-repo workspace where a
// Roslyn cold start dominates latency and adds nothing for a read-only lookup.
//
// The index stores one row per file, so every query below is a small indexed
// SQLite read rather than a load of the whole cache. What the index cannot
// answer is anything requiring a cross-file reference graph (find references,
// go to definition); those still need a live language server.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Cli;

internal static class SymbolsCommand
{
    /// <summary>Must match LanguageServerManager.SymbolCacheVersion or the cache reads as stale.</summary>
    private const int SymbolCacheVersion = 1;

    private const string CacheFilename = "symbols.json";

    private static readonly string[] KnownLanguages = ["csharp", "typescript"];

    public static Command Create()
    {
        var command = new Command("symbols")
        {
            Description = "Query the symbol index directly (read-only, no language server)",
        };

        command.Add(CreateStatsCommand());
        command.Add(CreateFindCommand());
        command.Add(CreateOverviewCommand());
        command.Add(CreateFilesCommand());

        return command;
    }

    // -- symbols stats --

    private static Command CreateStatsCommand()
    {
        var projectOption = ProjectOption();
        var command = new Command("stats") { Description = "Show what the symbol index contains" };
        command.Add(projectOption);

        command.SetAction(parseResult =>
        {
            string root = ResolveRoot(parseResult, projectOption);
            Console.WriteLine($"Project: {root}");

            var any = false;
            foreach (string language in DiscoverLanguages(root))
            {
                any = true;
                string dbPath = DatabasePath(root, language);
                var cache = OpenCache(root, language);
                if (cache is null)
                {
                    Console.WriteLine($"  {language,-12} index present but unreadable ({dbPath})");
                    continue;
                }

                long bytes = new FileInfo(dbPath).Length;
                Console.WriteLine(
                    $"  {language,-12} {cache.Count,7:N0} files   {bytes / 1024.0 / 1024.0,8:N1} MB");
            }

            if (!any)
            {
                Console.Error.WriteLine(
                    $"No symbol index found under {Path.Combine(root, ".serena", "cache")}.");
                Console.Error.WriteLine("Build one with: serena project index <path>");
                return 1;
            }

            return 0;
        });

        return command;
    }

    // -- symbols find --

    private static Command CreateFindCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "Symbol name to look for" };
        var projectOption = ProjectOption();
        var languageOption = LanguageOption();
        var substringOption = new Option<bool>("--substring")
        {
            Description = "Match any symbol whose name contains the term",
        };
        var pathOption = new Option<string?>("--path")
        {
            Description = "Restrict results to files under this path",
        };
        var maxOption = new Option<int>("--max")
        {
            Description = "Maximum symbols to print (-1 for all)",
            DefaultValueFactory = _ => 50,
        };
        var jsonOption = JsonOption();

        var command = new Command("find")
        {
            Description = "Find symbols by name using the indexed name table",
        };
        command.Add(nameArg);
        command.Add(projectOption);
        command.Add(languageOption);
        command.Add(substringOption);
        command.Add(pathOption);
        command.Add(maxOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            string root = ResolveRoot(parseResult, projectOption);
            if (WarnIfNoIndex(root))
            {
                return 1;
            }

            string name = parseResult.GetValue(nameArg)!;
            bool substring = parseResult.GetValue(substringOption);
            string? pathFilter = ResolvePathFilter(root, parseResult.GetValue(pathOption));
            int max = parseResult.GetValue(maxOption);
            bool asJson = parseResult.GetValue(jsonOption);

            var hits = new List<SymbolHit>();
            foreach (string language in TargetLanguages(root, parseResult, languageOption))
            {
                var cache = OpenCache(root, language);
                if (cache is null)
                {
                    continue;
                }

                if (!cache.TryGetCandidatePaths(name, substring, pathFilter, out var candidatePaths))
                {
                    continue;
                }

                foreach (string candidate in candidatePaths)
                {
                    var symbols = cache.TryGetUnchecked(candidate);
                    if (symbols is null)
                    {
                        continue;
                    }

                    // The name table is per-file, so a candidate file may contain
                    // unrelated symbols. Filter the tree down to real matches.
                    Collect(symbols, name, substring, language, root, hits);
                }
            }

            hits.Sort(static (a, b) =>
            {
                int byName = string.Compare(a.NamePath, b.NamePath, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            });

            return Report(hits, max, asJson, $"No indexed symbol matches '{name}'.");
        });

        return command;
    }

    // -- symbols overview --

    private static Command CreateOverviewCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "File whose symbols to outline" };
        var projectOption = ProjectOption();
        var depthOption = new Option<int>("--depth")
        {
            Description = "Nesting levels to print (-1 for all)",
            DefaultValueFactory = _ => 2,
        };
        var jsonOption = JsonOption();

        var command = new Command("overview")
        {
            Description = "Outline the symbols of one indexed file",
        };
        command.Add(fileArg);
        command.Add(projectOption);
        command.Add(depthOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            string root = ResolveRoot(parseResult, projectOption);
            if (WarnIfNoIndex(root))
            {
                return 1;
            }

            string file = Path.GetFullPath(parseResult.GetValue(fileArg)!);
            int depth = parseResult.GetValue(depthOption);
            bool asJson = parseResult.GetValue(jsonOption);

            foreach (string language in DiscoverLanguages(root))
            {
                var cache = OpenCache(root, language);
                var symbols = cache?.TryGetUnchecked(file);
                if (symbols is null)
                {
                    continue;
                }

                if (asJson)
                {
                    var flat = new List<SymbolHit>();
                    Collect(symbols, name: null, substring: false, language, root, flat);
                    Console.WriteLine(JsonSerializer.Serialize(flat, JsonOptions));
                    return 0;
                }

                Print(symbols, indent: 0, depth);
                return 0;
            }

            Console.Error.WriteLine($"'{file}' is not in the symbol index.");
            Console.Error.WriteLine("Index it with: serena project index <path>");
            return 1;
        });

        return command;
    }

    // -- symbols files --

    private static Command CreateFilesCommand()
    {
        var patternArg = new Argument<string?>("pattern")
        {
            Description = "Case-insensitive substring of the path",
            DefaultValueFactory = _ => null,
        };
        var projectOption = ProjectOption();
        var languageOption = LanguageOption();
        var maxOption = new Option<int>("--max")
        {
            Description = "Maximum paths to print (-1 for all)",
            DefaultValueFactory = _ => 100,
        };

        var command = new Command("files") { Description = "List indexed files" };
        command.Add(patternArg);
        command.Add(projectOption);
        command.Add(languageOption);
        command.Add(maxOption);

        command.SetAction(parseResult =>
        {
            string root = ResolveRoot(parseResult, projectOption);
            if (WarnIfNoIndex(root))
            {
                return 1;
            }

            string? pattern = parseResult.GetValue(patternArg);
            int max = parseResult.GetValue(maxOption);

            var paths = new List<string>();
            foreach (string language in TargetLanguages(root, parseResult, languageOption))
            {
                var cache = OpenCache(root, language);
                if (cache is null)
                {
                    continue;
                }

                foreach (string key in cache.Keys)
                {
                    if (pattern is null || key.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(Relative(root, key));
                    }
                }
            }

            paths.Sort(StringComparer.OrdinalIgnoreCase);

            if (paths.Count == 0)
            {
                Console.Error.WriteLine("No indexed file matches.");
                return 1;
            }

            int shown = max < 0 ? paths.Count : Math.Min(max, paths.Count);
            for (int i = 0; i < shown; i++)
            {
                Console.WriteLine(paths[i]);
            }

            if (shown < paths.Count)
            {
                Console.Error.WriteLine($"({paths.Count - shown} more; raise --max to see them)");
            }

            return 0;
        });

        return command;
    }

    // -- shared helpers --

    private static Option<string?> ProjectOption() =>
        new("--project") { Description = "Project root holding .serena/cache (default: current directory)" };

    private static Option<string?> LanguageOption() =>
        new("--lang") { Description = "Restrict to one indexed language (csharp, typescript)" };

    private static Option<bool> JsonOption() =>
        new("--json") { Description = "Emit JSON instead of text" };

    private static string ResolveRoot(ParseResult parseResult, Option<string?> projectOption) =>
        Path.GetFullPath(parseResult.GetValue(projectOption) ?? ".");

    /// <summary>
    /// Resolves <c>--path</c> against the project root when it is relative, because the
    /// index stores absolute paths and a relative filter would match nothing at all.
    /// A silently empty result is indistinguishable from "the symbol does not exist",
    /// so a filter that cannot match anything is reported rather than swallowed.
    /// </summary>
    private static string? ResolvePathFilter(string root, string? pathFilter)
    {
        if (string.IsNullOrWhiteSpace(pathFilter))
        {
            return null;
        }

        var resolved = Path.IsPathRooted(pathFilter)
            ? Path.GetFullPath(pathFilter)
            : Path.GetFullPath(Path.Combine(root, pathFilter));

        if (!Directory.Exists(resolved) && !File.Exists(resolved))
        {
            Console.Error.WriteLine(
                $"WARNING: --path '{pathFilter}' resolved to '{resolved}', which does not exist. "
                + "Results will be empty; check the path rather than concluding the symbol is absent.");
        }

        return resolved;
    }

    private static string DatabasePath(string root, string language) =>
        Path.Combine(root, ".serena", "cache", language, "symbols.db");

    private static IEnumerable<string> DiscoverLanguages(string root) =>
        KnownLanguages.Where(language => File.Exists(DatabasePath(root, language)));

    /// <summary>
    /// Distinguishes "no index at this root" from "no such symbol". Without this, running
    /// from the wrong directory reports an empty result, which reads as a confident
    /// "that symbol does not exist" when in fact nothing was searched.
    /// </summary>
    private static bool WarnIfNoIndex(string root)
    {
        if (DiscoverLanguages(root).Any())
        {
            return false;
        }

        Console.Error.WriteLine(
            $"ERROR: no symbol index under {Path.Combine(root, ".serena", "cache")}. "
            + "Nothing was searched. Pass --project <workspace-root>, or build an index with "
            + "'serena project index <path>'.");
        return true;
    }

    private static IEnumerable<string> TargetLanguages(
        string root,
        ParseResult parseResult,
        Option<string?> languageOption)
    {
        string? requested = parseResult.GetValue(languageOption);
        return requested is null
            ? DiscoverLanguages(root)
            : DiscoverLanguages(root).Where(l => l.Equals(requested, StringComparison.OrdinalIgnoreCase));
    }

    private static SymbolCache<UnifiedSymbolInformation[]>? OpenCache(string root, string language)
    {
        var cache = new SymbolCache<UnifiedSymbolInformation[]>(
            Path.Combine(root, ".serena", "cache", language),
            CacheFilename,
            SymbolCacheVersion,
            NullLogger<SymbolCache<UnifiedSymbolInformation[]>>.Instance);

        return cache.Load() ? cache : null;
    }

    /// <summary>
    /// Flattens a symbol tree, keeping entries whose name matches. A null
    /// <paramref name="name"/> keeps everything, which is what the file outline wants.
    /// </summary>
    private static void Collect(
        IReadOnlyList<UnifiedSymbolInformation> symbols,
        string? name,
        bool substring,
        string language,
        string root,
        List<SymbolHit> hits)
    {
        foreach (var symbol in symbols)
        {
            bool matches = name is null
                || (substring
                    ? symbol.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                    : symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (matches)
            {
                hits.Add(new SymbolHit(
                    symbol.NamePath,
                    symbol.Kind.ToString(),
                    Relative(root, LocalPath(symbol)),
                    (symbol.StartLine ?? -1) + 1,
                    language,
                    symbol.Detail));
            }

            if (symbol.Children.Count > 0)
            {
                Collect(symbol.Children, name, substring, language, root, hits);
            }
        }
    }

    private static int Report(List<SymbolHit> hits, int max, bool asJson, string emptyMessage)
    {
        if (hits.Count == 0)
        {
            Console.Error.WriteLine(emptyMessage);
            return 1;
        }

        int shown = max < 0 ? hits.Count : Math.Min(max, hits.Count);

        if (asJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(hits.Take(shown), JsonOptions));
        }
        else
        {
            for (int i = 0; i < shown; i++)
            {
                var hit = hits[i];
                Console.WriteLine($"{hit.Kind,-12} {hit.NamePath}");
                Console.WriteLine($"             {hit.File}:{hit.Line}");
            }
        }

        if (shown < hits.Count)
        {
            Console.Error.WriteLine($"({hits.Count - shown} more; raise --max to see them)");
        }

        return 0;
    }

    /// <summary>
    /// Prints a symbol outline. <paramref name="level"/> is tracked separately from
    /// <paramref name="indent"/> so namespace wrappers do not consume a depth level:
    /// otherwise a C# file spends its whole budget on the namespace and stops before
    /// class members, while the equivalent TypeScript file shows them.
    /// </summary>
    private static void Print(
        IReadOnlyList<UnifiedSymbolInformation> symbols,
        int indent,
        int depth,
        int level = 0)
    {
        if (depth >= 0 && level >= depth)
        {
            return;
        }

        string prefix = new(' ', indent * 2);
        foreach (var symbol in symbols)
        {
            Console.WriteLine($"{prefix}{symbol.Kind} {symbol.Name} (line {(symbol.StartLine ?? -1) + 1})");
            if (symbol.Children.Count > 0)
            {
                Print(symbol.Children, indent + 1, depth, level + (IsContainer(symbol) ? 0 : 1));
            }
        }
    }

    private static bool IsContainer(UnifiedSymbolInformation symbol) =>
        symbol.Kind is Lsp.Protocol.Types.SymbolKind.Namespace
            or Lsp.Protocol.Types.SymbolKind.Module
            or Lsp.Protocol.Types.SymbolKind.Package;

    private static string LocalPath(UnifiedSymbolInformation symbol)
    {
        string? uri = symbol.Location?.Uri;
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile
            ? parsed.LocalPath
            : uri;
    }

    private static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        try
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private sealed record SymbolHit(
        string NamePath,
        string Kind,
        string File,
        int Line,
        string Language,
        string? Detail);
}
