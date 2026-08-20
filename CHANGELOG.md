# Changelog

## [0.2.1]

### Fixed
Two silent-failure traps in `symbols`, both of which made "nothing was searched" look
identical to "that symbol does not exist":

- **A relative `--path` matched nothing.** The index stores absolute paths, so
  `--path SomeRepo` could never match. It is now resolved against the
  project root, and a filter pointing at a nonexistent path is reported instead of
  silently returning an empty result.
- **Running from a directory with no index reported "No indexed symbol matches".**
  `find`, `overview` and `files` now detect the absence of an index up front and say so,
  naming the expected cache location and how to fix it.

## [0.2.0]

### Added
- **`serena symbols` command group: read-only queries answered from the on-disk
  symbol index, with no language server started.** On a large multi-repo workspace a
  Roslyn cold start costs 10 to 15 seconds and contributes nothing to a lookup that
  the index already answers. These read `.serena/cache/<language>/symbols.db`
  directly, one small indexed SQLite read per query:
  - `symbols stats` shows indexed file counts and index size per language.
  - `symbols find <name> [--substring] [--path P] [--lang L] [--max N] [--json]`
    resolves names through the indexed `symbol_names` table.
  - `symbols overview <file> [--depth N] [--json]` outlines one file's symbols.
    Namespace wrappers do not consume a depth level, so `--depth 2` shows class
    members in C# just as it does in TypeScript.
  - `symbols files [pattern] [--lang L] [--max N]` lists indexed files.

  Cross-file relationship queries (find references, go to definition) still require a
  live language server, because the index stores document symbols and not a
  reference graph.

- **Solution-scoped indexing.** `project index --solution <path>` parses the solution,
  follows resolvable transitive `ProjectReference` entries, and indexes only those
  project trees plus explicitly linked sources, persisting the scope under
  `csharp.scope.solutions`. `ProjectReference` paths containing unevaluated MSBuild
  properties or wildcards need Roslyn and are excluded from the cache-only closure.
- **Configured languages in `.serena/project.yml`.** An explicit `languages` field is
  honoured when present, and auto-detection remains the behaviour when it is absent, so
  existing projects are unaffected.

### Changed
- **The symbol cache is now a SQLite database** (`symbols.db`, schema v2) instead of a
  single JSON document. This is what makes index-only querying practical on a large
  workspace: reads are per-file indexed lookups rather than loading the whole cache, and
  a `symbol_names` table keyed on the leaf name (`COLLATE NOCASE`) turns name resolution
  into an indexed query.
  - Legacy `symbols.json` caches migrate on load.
  - v1 path-keyed name indexes are normalised to entry ids on load.
  - Cache keys are path-normalised, collapsing duplicates that differed only by
    separator or case.
  - Invalidations persist incrementally instead of forcing a full cache rewrite.
  - Memory: the previous design kept two full copies of a large cache in memory during
    save; a very large repository could hold several hundred MB twice.

### Fixed
- **Writes invalidate the symbol cache without starting a language server.**
  `replace_content`, `create_text_file` (overwrite), `delete_lines` and `insert_at_line`
  each drop the affected cache entry, so a stale entry can no longer outlive an edit.
- **File discovery.** Git-ignored and generated paths are skipped, and files with an
  unrecognised extension are no longer treated as C#.
- **CRLF handling in `search_for_pattern`.** Output no longer contains doubled carriage
  returns on CRLF files, LF-only files are unaffected, and overlapping context windows
  merge instead of repeating lines.

All notable changes to **Serena.DotNet** are recorded here. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [0.1.5] - Large-repository indexing and semantic honesty

### Added

- `serena-dotnet project index <repo-root> --solution <sln>` indexes the solution's C# project trees, resolvable transitive `ProjectReference` dependencies, and linked source files into the repository-level cache, then persists that solution as the active Roslyn scope.
- Indexed SQLite symbol storage at `.serena/cache/<lang>/symbols.db`, including a declaration leaf-name index for exact and substring candidate lookup.
- Automatic one-time migration of legacy `symbols.json` caches.
- `Partial` workspace readiness plus captured Roslyn warning/error messages, actual solution project counts, and explicit completeness metadata on reference results.
- Per-language `ls_specific_settings.<language>.server_path` support and `doctor` checks/install hints for all 13 registered backends.

### Changed

- Large unscoped C# repositories are no longer opened wholesale. Repositories with multiple solutions or more than 50 unscoped C# projects require `set_active_solution` first.
- Symbol-cache reads and writes are per-entry and transactional instead of deserializing or rewriting one monolithic JSON file.
- Repeat indexing validates all fingerprints in one sequential cache pass and skips language-server startup entirely when the selected files are current.
- Workspace warning capture is de-duplicated and bounded; partial reference responses include three short samples instead of repeating the full warning list.
- `search_for_pattern` prunes ignored directories before descent, merges overlapping context blocks, and defaults to a 100,000-character response cap instead of 2,000,000.
- README now explains when Serena is structurally better than grep, how lazy non-C# server installation works, and the focused Patient Window-style workflow for a large monorepo.

### Security

- SQLite uses `Microsoft.Data.Sqlite` 10.0.10 with `SQLitePCLRaw` 2.1.12, avoiding the vulnerable 2.1.11 native bundle.

## [0.1.4] - Indexer ignore fixes

- Fixed language detection so unknown or extensionless files no longer default to C#.
- Fixed source discovery to prune ignored directories while walking the tree instead of reporting raw glob results.
- Built-in ignore rules now apply to ignored path segments anywhere in the path, including nested `.git`, `bin`, `obj`, and `node_modules` directories.
- Added regression coverage for nested Git object paths, Visual Studio `[Bb]in/` and `[Oo]bj/` ignore patterns, and unknown-extension language detection.

## [0.1.3] - NuGet README refresh

- Republished the package with the corrected README and package metadata so NuGet.org displays the current documentation.

## [0.1.2] - Cache and MCP hardening

### Added

- `serena-dotnet project index-file <file>` for refreshing a single file in the symbol cache.
- Write-time symbol-cache invalidation for `create_text_file`, `replace_content`, line edits, and symbol edits.
- Persistent invalidation journal at `.serena/cache/<lang>/invalidated-paths.txt` so stale symbols remain suppressed across process restarts.
- `SERENA_LSP_SHUTDOWN_TIMEOUT_SECONDS` to control graceful language-server shutdown before force-killing the process tree.

### Changed

- Plain file/text writes invalidate the affected cache entry without cold-starting the language server.
- If the matching language server is already running and Ready, writes refresh only the edited file instead of reindexing the project.
- Symbol-cache loading collapses duplicate normalized file keys from older cache files.
- README now documents explicit MCP project activation, Windows `SystemRoot` config, language scoping, and the current `register <path> --name <n>` syntax.

## [0.1.1] - Docs

- **Docs only.** Fixed the MCP client configuration example in `README.md` to drop the hardcoded `cwd`.

## [0.1.0] - Public preview

First public release. Reset from internal `1.0.x` series to signal preview status.

### Highlights

- **C#/.NET focused** MCP coding agent built around Microsoft's official Roslyn language server (`Microsoft.CodeAnalysis.LanguageServer`).
- **Single-binary install** as a .NET global tool — no Python, no `uv`, no virtualenv.
- **38 tools** covering file operations, symbol navigation/edit, line edits, memory, project activation, language-server lifecycle, and onboarding workflow.
- **Human-readable on-disk symbol cache** at `.serena/cache/<lang>/symbols.json`, fingerprinted by file size + LastWriteTimeUtc.
- **Lazy LSP startup** — Roslyn doesn't spin up if the symbol cache can answer.
- **Outline-first responses** for `find_symbol include_body=true`: every match's `name_path` + `file:line-range` + `body_chars` is emitted inline before the bodies, so even when the MCP client spills the response to a sidecar file, the agent retains navigability. Bodies past a per-body cap (`SERENA_MAX_INLINE_BODY_BYTES`, default 8192) are replaced with a re-call hint.
- **`match_overloads`** parameter on `find_symbol` — single call returns every symbol with the same leaf name regardless of parent path.
- **Did-you-mean fallback** — when `find_symbol` doesn't match exactly, returns qualified candidates with the same leaf name (no extra LSP calls).
- **Auto-descend overview** — `get_symbols_overview` automatically descends past pure container kinds (Namespace/Module/Package), so depth=0 returns the actual class/method outline on .NET source. Opt out with `auto_descend_containers: false`.
- **BOM-preserving file edits** — every edit tool sniffs the original file's first 3 bytes and emits a UTF-8 BOM only if it was already there.
- **Force-kill LSP on tool timeout** — `SERENA_TOOL_TIMEOUT_SECONDS` enforces a hard ceiling and rebuilds the Roslyn process tree on expiry.
- **Pre-flight readiness gate** — `find_symbol` / `find_referencing_symbols` throw a structured warming response with workspace state and uptime when the LSP isn't Ready, so the agent gets actionable advice instead of a fake empty result.
- **Cache-first project-wide search** with `serena-dotnet project index .`.

### Tunable env vars

| Variable | Default | Range |
|---|---|---|
| `SERENA_TOOL_TIMEOUT_SECONDS` | 90 | 5–3600 |
| `SERENA_LSP_REQUEST_TIMEOUT_SECONDS` | 60 | 5–1800 |
| `SERENA_LSP_PARALLELISM` | 2 | 1–16 |
| `SERENA_SEARCH_PARALLELISM` | min(CPU, 16) | 1–32 |
| `SERENA_MAX_INLINE_BODY_BYTES` | 8192 | 512–1048576 |

### Acknowledgement

Inspired by and indebted to [oraios/serena](https://github.com/oraios/serena). The agent-facing tool surface, the symbol-aware approach, the project activation model, and the memory system all come from their design. This project re-implements that experience in .NET for the C#/Roslyn audience.
