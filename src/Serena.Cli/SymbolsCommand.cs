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
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Serena.Lsp;
using Serena.Lsp.Caching;
using Serena.Lsp.Client;

namespace Serena.Cli;

internal static class SymbolsCommand
{
    /// <summary>Must match LanguageServerManager.SymbolCacheVersion or the cache reads as stale.</summary>
    private const int SymbolCacheVersion = 2;

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
        command.Add(CreateReferencesCommand());
        command.Add(CreateInventoryCommand("config",
            "Configuration keys (appsettings, Octodeploy, web.config) and MSBuild properties whose key matches a term",
            ["json", "property"]));
        command.Add(CreateInventoryCommand("packages",
            "Package and project references whose name matches a term",
            ["package", "project"]));

        return command;
    }

    // -- symbols references --

    private static Command CreateReferencesCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "Identifier to look for (exact, case-insensitive)" };
        var projectOption = ProjectOption();
        var languageOption = LanguageOption();
        var pathOption = new Option<string?>("--path")
        {
            Description = "Restrict results to files under this path",
        };
        var maxOption = new Option<int>("--max")
        {
            Description = "Maximum occurrences to print (-1 for all)",
            DefaultValueFactory = _ => 100,
        };
        var jsonOption = JsonOption();

        var command = new Command("references")
        {
            Description = "Where an identifier occurs, from the index built at index time: file, line, " +
                          "enclosing symbol and the source line. Textual, so same-named members of unrelated " +
                          "types are all listed; declarations are marked. Needs no language server.",
        };
        command.Add(nameArg);
        command.Add(projectOption);
        command.Add(languageOption);
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
            string? pathFilter = ResolvePathFilter(root, parseResult.GetValue(pathOption));
            int max = parseResult.GetValue(maxOption);
            bool asJson = parseResult.GetValue(jsonOption);

            var hits = new List<ReferenceHit>();
            bool anyOccurrenceTable = false;
            foreach (string language in TargetLanguages(root, parseResult, languageOption))
            {
                var cache = OpenCache(root, language);
                if (cache is null)
                {
                    continue;
                }
                if (cache.Occurrences.RowCount() > 0)
                {
                    anyOccurrenceTable = true;
                }

                foreach (var occurrence in cache.Occurrences.Query(name, pathFilter))
                {
                    var symbols = cache.TryGetUnchecked(occurrence.Path);
                    string relative = Relative(root, occurrence.Path);
                    string[]? sourceLines = ReadSourceLines(root, occurrence.Path, relative);
                    foreach (int line in occurrence.Lines)
                    {
                        var enclosing = symbols is null ? null : FindEnclosing(symbols, line - 1);
                        bool isDeclaration = symbols is not null && IsDeclaredAt(symbols, name, line - 1);
                        string? snippet = sourceLines is not null && line - 1 < sourceLines.Length
                            ? sourceLines[line - 1].Trim()
                            : null;
                        hits.Add(new ReferenceHit(relative, line, enclosing?.NamePath, isDeclaration, snippet, language));
                    }
                }
            }

            if (hits.Count == 0)
            {
                Console.Error.WriteLine(anyOccurrenceTable
                    ? $"No indexed occurrence of '{name}'."
                    : "The index has no occurrence table yet (built before 0.6.0). Rerun 'serena-dotnet project index <path>' to add it.");
                return 1;
            }

            hits.Sort(static (a, b) =>
            {
                int byFile = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
                return byFile != 0 ? byFile : a.Line.CompareTo(b.Line);
            });

            int shown = max < 0 ? hits.Count : Math.Min(max, hits.Count);
            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(hits.Take(shown), JsonOptions));
            }
            else
            {
                foreach (var hit in hits.Take(shown))
                {
                    string where = hit.ContainingSymbol ?? "(file scope)";
                    string marker = hit.IsDeclaration ? "  [declaration]" : string.Empty;
                    Console.WriteLine($"{hit.File}:{hit.Line}  {where}{marker}");
                    if (hit.Snippet is not null)
                    {
                        Console.WriteLine($"      {hit.Snippet}");
                    }
                }
                int files = hits.Select(h => h.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                Console.WriteLine(shown < hits.Count
                    ? $"{hits.Count} occurrence(s) in {files} file(s); showing {shown} (use --max)"
                    : $"{hits.Count} occurrence(s) in {files} file(s)");
            }
            return 0;
        });

        return command;
    }

    private static readonly Dictionary<string, string[]?> s_sourceLineCache = new(StringComparer.OrdinalIgnoreCase);

    private static string[]? ReadSourceLines(string root, string storedPath, string relative)
    {
        if (s_sourceLineCache.TryGetValue(storedPath, out var cached))
        {
            return cached;
        }
        string[]? lines = null;
        try
        {
            string local = File.Exists(storedPath) ? storedPath : Path.Combine(root, relative);
            if (File.Exists(local))
            {
                lines = File.ReadAllLines(local);
            }
        }
        catch
        {
            lines = null;
        }
        s_sourceLineCache[storedPath] = lines;
        return lines;
    }

    /// <summary>Deepest symbol whose range covers the zero-based line, or null.</summary>
    internal static UnifiedSymbolInformation? FindEnclosing(IReadOnlyList<UnifiedSymbolInformation> symbols, int line)
    {
        foreach (var symbol in symbols)
        {
            int? start = symbol.StartLine;
            int? end = symbol.EndLine;
            if (start is null || end is null || line < start || line > end)
            {
                continue;
            }
            return FindEnclosing(symbol.Children, line) ?? symbol;
        }
        return null;
    }

    private static bool IsDeclaredAt(IReadOnlyList<UnifiedSymbolInformation> symbols, string name, int line)
    {
        foreach (var symbol in symbols)
        {
            string leaf = symbol.Name;
            int bracket = leaf.IndexOf('[');
            if (bracket >= 0)
            {
                leaf = leaf[..bracket];
            }
            int declaredLine = symbol.SelectionRange?.Start.Line ?? symbol.StartLine ?? -1;
            if (declaredLine == line && leaf.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (symbol.Children.Count > 0 && IsDeclaredAt(symbol.Children, name, line))
            {
                return true;
            }
        }
        return false;
    }

    private sealed record ReferenceHit(
        string File,
        int Line,
        string? ContainingSymbol,
        bool IsDeclaration,
        string? Snippet,
        string Language);

    // -- symbols config / symbols packages --

    private static Command CreateInventoryCommand(string name, string description, string[] kinds)
    {
        var termArg = new Argument<string>("term") { Description = "Key or name to look for (substring unless --exact)" };
        var projectOption = ProjectOption();
        var pathOption = new Option<string?>("--path")
        {
            Description = "Restrict results to files under this path",
        };
        var exactOption = new Option<bool>("--exact") { Description = "Match the whole key, not a substring" };
        var maxOption = new Option<int>("--max")
        {
            Description = "Maximum rows to print (-1 for all)",
            DefaultValueFactory = _ => 100,
        };
        var jsonOption = JsonOption();

        var command = new Command(name) { Description = description + ". From the inventory built by 'project index' or 'project inventory'; no language server." };
        command.Add(termArg);
        command.Add(projectOption);
        command.Add(pathOption);
        command.Add(exactOption);
        command.Add(maxOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            string root = ResolveRoot(parseResult, projectOption);
            var inventory = new ProjectInventory(Path.Combine(root, ".serena", "cache"), NullLogger.Instance);
            if (!inventory.Exists)
            {
                Console.Error.WriteLine(
                    $"ERROR: no inventory under {Path.Combine(root, ".serena", "cache")}. " +
                    "Build one with 'serena-dotnet project inventory <path>' (seconds) or 'serena-dotnet project index <path>'.");
                return 1;
            }

            string term = parseResult.GetValue(termArg)!;
            string? pathFilter = ResolvePathFilter(root, parseResult.GetValue(pathOption));
            bool exact = parseResult.GetValue(exactOption);
            int max = parseResult.GetValue(maxOption);
            bool asJson = parseResult.GetValue(jsonOption);

            var rows = inventory.Query(term, !exact, kinds, pathFilter, max < 0 ? 0 : max);
            if (rows.Count == 0)
            {
                Console.Error.WriteLine($"No inventory row matches '{term}'.");
                return 1;
            }

            if (asJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    rows.Select(r => new { r.Kind, r.Key, r.Value, File = Relative(root, r.Path) }), JsonOptions));
            }
            else
            {
                foreach (var row in rows)
                {
                    string value = row.Value is null ? string.Empty : $" = {row.Value}";
                    Console.WriteLine($"{row.Kind,-8} {row.Key}{value}");
                    Console.WriteLine($"         {Relative(root, row.Path)}");
                }
                Console.WriteLine($"{rows.Count} row(s)");
            }
            return 0;
        });

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
                long occurrenceRows = cache.Occurrences.RowCount();
                Console.WriteLine(
                    $"  {language,-12} {cache.Count,7:N0} files   {bytes / 1024.0 / 1024.0,8:N1} MB   " +
                    (occurrenceRows > 0
                        ? $"{occurrenceRows:N0} occurrence rows"
                        : "no occurrence table (index predates 0.6.0; rerun project index for symbols references)"));
            }

            var inventory = new ProjectInventory(Path.Combine(root, ".serena", "cache"), NullLogger.Instance);
            var inventoryStats = inventory.Stats();
            Console.WriteLine(inventoryStats.Count > 0
                ? "  inventory    " + string.Join(", ", inventoryStats.OrderBy(p => p.Key).Select(p => $"{p.Value:N0} {p.Key}"))
                : "  inventory    none (run 'serena-dotnet project inventory <path>')");

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

                // A prebuilt index shipped in a relocatable bundle stores paths from the
                // machine that built it, so an exact match on the local path misses. Retry
                // with the path translated into the stored form before giving up, otherwise
                // a file that is indexed reports as absent.
                if (symbols is null && cache is not null)
                {
                    string? stored = ToStoredPath(root, cache, file);
                    if (stored is not null)
                    {
                        symbols = cache.TryGetUnchecked(stored);
                    }
                }

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
            Console.Error.WriteLine(
                "Check the path, and confirm the index covers it with: " +
                "serena-dotnet symbols files \"*<filename>*\" --project <root>");
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
                    if (MatchesFilePattern(key, pattern))
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
                    : symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                      || (!SymbolNames.HasTypeParameters(name)
                          && SymbolNames.StripTypeParameters(symbol.Name).Equals(name, StringComparison.OrdinalIgnoreCase)));

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

    /// <summary>
    /// Matches an indexed path against a caller-supplied pattern. A pattern containing
    /// <c>*</c> or <c>?</c> is treated as a glob over the whole path, which is what the
    /// argument name implies; anything else is a plain substring. Without the glob case a
    /// natural pattern like <c>*Thumbnail*.cs</c> silently matches nothing, which reads
    /// exactly like "the index does not contain it".
    /// </summary>
    private static bool MatchesFilePattern(string key, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        if (pattern.IndexOf('*') < 0 && pattern.IndexOf('?') < 0)
        {
            return key.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        string regex = "^" + Regex.Escape(pattern.Replace(BackSlash, ForwardSlash))
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return Regex.IsMatch(key.Replace(BackSlash, ForwardSlash), regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Renders a stored path relative to <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// The index stores absolute paths from the machine that built it, so a prebuilt index
    /// shipped inside a relocatable bundle will hold paths that do not exist here. When the
    /// stored path is not under <paramref name="root"/>, recover the repository-relative
    /// part instead of printing a path the caller cannot open.
    /// </remarks>
    private static string Relative(string root, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Normalise separators first. An index built on Windows stores backslash-separated,
        // drive-qualified paths, and on Unix a backslash is an ordinary filename character,
        // so nothing downstream would ever split them.
        string normalized = path.Replace(BackSlash, ForwardSlash);

        // Strip the root the index was built at before trying GetRelativePath. Order
        // matters on Unix: "C:/x/y" has no leading slash, so it is a *relative* path there,
        // and GetRelativePath resolves it against the current directory and returns
        // something that looks relative but resolves nowhere.
        string? buildRoot = BuildRootFor(root, normalized);
        if (buildRoot is not null
            && normalized.StartsWith(buildRoot + ForwardSlash, StringComparison.OrdinalIgnoreCase))
        {
            return normalized[(buildRoot.Length + 1)..]
                .Replace(ForwardSlash, Path.DirectorySeparatorChar);
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

    // How many stored paths to sample when deriving the build root. One is
    // usually enough; more tolerates an install missing some directories.
    private const int BuildRootSamples = 64;
    private const char BackSlash = '\\';
    private const char ForwardSlash = '/';

    // Deriving the build root costs a directory probe per path segment, so remember it.
    // One index has one build root, and the CLI is a single short-lived process.
    private static string? _buildRootFor;
    private static string? _buildRoot;

    /// <summary>
    /// The root the index was built at, recovered from the data rather than assumed: the
    /// first segment of a stored path that names a directory under <paramref name="root"/>
    /// marks the boundary, and everything before it is the build root. Null when it cannot
    /// be established, in which case callers should leave paths untouched.
    /// </summary>
    private static string? BuildRootFor(string root, string normalizedStoredPath)
    {
        if (string.Equals(_buildRootFor, root, StringComparison.OrdinalIgnoreCase)
            && _buildRoot is not null)
        {
            return _buildRoot;
        }

        string[] segments = normalizedStoredPath.Split(ForwardSlash);
        for (int i = 1; i < segments.Length - 1; i++)
        {
            // Skip empties and anything drive-qualified: Path.Combine treats "C:" as
            // rooted and returns it verbatim, so probing it would spuriously succeed and
            // yield an empty prefix.
            if (segments[i].Length == 0 || segments[i].Contains(':'))
            {
                continue;
            }

            if (Directory.Exists(Path.Combine(root, segments[i])))
            {
                string candidate = string.Join(ForwardSlash, segments.Take(i));
                if (candidate.Length > 0)
                {
                    _buildRootFor = root;
                    _buildRoot = candidate;
                }

                break;
            }
        }

        return _buildRoot;
    }

    /// <summary>
    /// Translates a path on this machine into the form the index stored it under, so a
    /// prebuilt index can be queried by path after the bundle has been relocated.
    /// </summary>
    private static string? ToStoredPath(
        string root,
        SymbolCache<UnifiedSymbolInformation[]> cache,
        string localFullPath)
    {
        string? buildRoot = null;
        foreach (string key in cache.Keys.Take(BuildRootSamples))
        {
            buildRoot = BuildRootFor(root, key.Replace(BackSlash, ForwardSlash));
            if (buildRoot is not null)
            {
                break;
            }
        }

        if (buildRoot is null)
        {
            return null;
        }

        string relative = Path.GetRelativePath(root, localFullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return buildRoot + ForwardSlash + relative.Replace(BackSlash, ForwardSlash);
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
