# Changelog

## [0.7.2]

### Changed
- **Onboarding instructions are imperative.** A session given only "onboard this repository" read the
  generated overview, concluded that most steps were answered and asked whether to write memories
  instead of writing them: the 0.6.2 wording ("skip the steps below that it answers") gave it the
  exit. The instructions now say to do every step in this turn, that the overview holds facts but no
  domain knowledge, patterns or conventions, that step 5 is required, and that onboarding is complete
  only when `check_onboarding_performed` lists the memories.

## [0.7.1]

Three more measured runs (serena 0.6.1, serena 0.7.0, and a second vanilla run that
delegated its test projects to sub-agents) showed what remained: edits racing on one
file, files created by other tools missing from the index, a generated overview nobody
read, and an activation that reset the server when a second caller activated the same
project.

### Changed
- **Activating the active project again is a reconcile, not a restart.** `activate_project`
  on the path that is already active keeps the caches, the pending outlines and a running
  Roslyn, reconciles the index with the disk and returns. A sub-agent that activates the
  project its parent works in no longer resets the parent's state. A path that does not
  exist fails without touching the active project (it used to dispose it first).
- **Edits to one file apply in sequence.** Every edit tool holds the file's lease from read
  to write (`FileWriteGate.LockAsync`), so `replace_symbol_body`, `apply_edits`,
  `replace_content`, the line tools and `create_text_file` issued in parallel on one file
  each see the previous result instead of overwriting it. Unrelated files never wait on
  each other.
- **`activate_project` shows the start of the generated overview** (first 40 lines) instead
  of pointing at it, and labels the memory list "read the project ones before exploring".
  Global memories filed under another project's folder are left out of the list.
- **Names match without arity.** `find_symbol`, `symbols find` and the name index treat
  `ToResult` as naming `ToResult<T>` and `ToResult<T, E>`; a pattern that carries type
  parameters still names exactly that one.

### Added
- **Project file watcher.** While a project with an index is active, files created, changed
  or deleted by anything other than serena's tools (an agent's built-in create tool, git,
  dotnet format) reach the index as they land: a C# file is outlined from its text, a
  vanished file is dropped, a folder that moves in has its files outlined, and a vanished
  folder prunes the index. `.git`, `.serena`, gitignored paths and `exclude_patterns` are
  not watched.

## [0.7.0]

Five measured runs of one refactor showed what agents use serena for: batch edits and the
occurrence index. The language server, which every outline depended on, spent the runs Loading
or Partial and silently failed to refresh edited files, and nobody called the semantic tools
that need it. So the everyday path no longer needs it.

### Changed
- **C# outlines come from the parser.** `project index`, `project index-file`, the refresh
  after every edit, the reconciliation at `activate_project` and a cache miss in `find_symbol`
  or `get_symbols_overview` all outline a C# file from its syntax tree (Microsoft.CodeAnalysis.CSharp),
  in process, in milliseconds, with no workspace load and no Loading or Partial state. The
  shape is the one Roslyn's server returns for `textDocument/documentSymbol`: names with type
  parameters, `name : type` and `name(params) : return` details, constructors as methods, fields
  as their declarators, local functions under their member, explicit implementations under
  their plain name, overload indices by document order, and the .NET Framework merge for files
  with target-framework conditionals. Checked against a server-built index of 1,560 files:
  1,556 identical by name and kind, 1,518 of 1,522 identical name paths; the four that differ
  are fields the server had named with an empty type.
- **An edit refreshes its outline synchronously.** `apply_edits` and the other edit tools
  rebuild the outline from the written text before returning; the pending set and the
  background drain added in 0.6.2 now serve only languages that still need a server.
- **`project index` is seconds, not minutes, for C#.** Roslyn is started only when the caller
  passes `--server` (the pre-0.7 path, kept for comparison) or for other languages.

### Added
- `project parser-check <path>`: compares the parser's outline of every indexed C# file with
  the outline stored in the index and lists the differences, so a server-built index can be
  checked against the parser before trusting the parser alone.
- `SymbolCache.ReconcileWithDisk` takes an optional outline builder, used for C#, so a changed
  or new file gets a fresh outline at activation instead of losing it until the next index.

### Unchanged
- `find_referencing_symbols`, `rename_symbol`, `get_file_diagnostics` and the diagnostics in
  `apply_edits` still use the language server; they are the semantic questions a syntax tree
  cannot answer. The server starts on the first such call, as before.

## [0.6.2]

The 0.6.1 rerun showed the occurrence index staying current while the outline refresh still
failed: the edit tool waited ten seconds for Roslyn to re-outline each changed file, Roslyn was
Partial on 77 projects with a build competing for the CPU, and every one of 57 attempts timed out
without a trace. Every agent in the round also spent its first minutes rediscovering the same
facts about the solution. Both are addressed here.

### Changed
- **Outline refresh is asynchronous.** An edit drops the stale outline, rebuilds the occurrence
  rows from the text and returns at once; the outline is rebuilt in the background with a
  60-second budget whenever the server is Ready or Partial. Files whose outline could not be
  rebuilt stay in a per-language pending set that is drained again when the server becomes
  usable and on every later edit, up to three attempts each. A file edited while its outline was
  being requested is dropped and queued again, so a stale outline is never stored.
- **Failures are recorded.** The refresher returns the reason (timeout, server error, file
  unreadable); the manager logs each failed attempt at Information and the give-up at Warning,
  and `get_language_server_status` reports `pending_outlines` per language.
- **The `apply_edits` index line** now appears only when a server is not usable, and says that the
  outline waits for the server while `find_occurrences` is already current.

### Added
- **Generated project overview.** `project index` ends by writing
  `.serena/memories/generated/project-overview.md` from the index and the inventory: solutions,
  projects and their folders, target frameworks, build and test commands and frameworks, the
  `.editorconfig` rules the build enforces as errors, the measured line-ending and BOM convention,
  the most depended-on projects, namespaces and the largest types per language, packages grouped
  by what they are, and the configuration sections. Facts only, every line a count, a name, a path
  or a version. `project overview <path>` rewrites it from the existing index without a server.
  `activate_project` points at it, `check_onboarding_performed` does not count it as onboarding,
  and the `onboarding` prompt tells the agent to read it first and write only what it cannot know.
- `ProjectInventory.All(kind)` for consumers that need every row of one kind.

## [0.6.1]

A measured session (round 4 of the tooling comparison, 8 Nov 2026) showed the index going
stale during a 35-minute refactor: the only save ran when the agent restarted the language
server, 79 edited files lost their outline for the rest of the session because the server was
still loading, and files deleted or created by other tools were never noticed. The cause was
the shape of the persistence code rather than any one bug, so this release restructures it.

### Changed
- **Write-behind persistence.** The symbol cache saves itself two seconds after the last
  mutation, flushes before any query that reads the database, and flushes on dispose. The
  language server manager no longer saves at stop, restart and recycle; those five call sites
  are gone, and so is the `invalidated-paths.txt` journal, which the write-behind save makes
  redundant.
- **`OccurrenceIndex` is its own class.** The occurrence tables, their schema and migrations,
  the text capture, the queries and the backfill moved out of the generic `SymbolCache<T>`
  into `cache.Occurrences`. The cache no longer inspects its type parameter at runtime to
  decide whether occurrences apply.
- **`restart_language_server` restarts.** It used to dispose the manager and leave the server
  NotStarted, so every later edit skipped its outline refresh until something else started the
  server. It now starts the servers that were running, with the project's current settings,
  and reports that they are warming up.
- **`apply_edits` says when the outline could not be rebuilt.** One line at the end of the
  result names the server state and the number of files whose occurrence rows were updated
  from text but whose outline waits for the server.

### Added
- **Deleted files are first class.** `find_occurrences` and `symbols references` leave out a
  file that no longer exists and drop its rows; `SymbolCache.PruneMissingFiles()` does the same
  for the whole index.
- **Reconciliation at `activate_project`.** When an index exists, activation compares it with
  the files on disk: deleted files are dropped, changed files lose their stale outline and get
  fresh occurrence rows from their text, and files new to a directory the index already covers
  get occurrence rows. Directories the index never covered are left alone, so a
  solution-scoped index stays scoped. The activation result reports the counts.
- `SymbolCache` reloads a database that appeared after it was first opened (an index built by
  another process), instead of reporting the cache absent for the rest of the session.

## [0.6.0]

For the read-only consumers of a prebuilt index (the HCW troubleshooter runs
`serena-dotnet symbols find` and `symbols overview` with no language server). In one
real investigation 22 text searches ran against 9 index lookups, and the blind spots behind
them were callers, csproj and package references, and JSON configuration. Both are now in
the index.

### Added
- **`symbols references <Name>`**: where an identifier occurs, from an occurrence table
  built at index time: file, one-based line, the enclosing symbol from the stored outline,
  a `[declaration]` marker and the source line. Textual, so same-named members of unrelated
  types are all listed, and it answers in milliseconds without Roslyn. `--path`, `--lang`,
  `--max`, `--json`.
- **`find_occurrences`** MCP tool: the same lookup for agents, with `exclude_declarations`,
  for the cheap first answer to "who calls this?" before a Roslyn-bound
  `find_referencing_symbols`.
- **Occurrence capture in every index path.** `project index`, `project index-file`, the
  inline refresh after an edit and the cache-miss retrieval all record the file's
  PascalCase identifiers (three characters or more, comments masked, string literals kept)
  beside its outline. The table is additive: an index built before 0.6.0 opens unchanged
  and reports "no occurrence table" in `symbols stats` and `symbols references`; the next
  `project index` backfills rows from the text of every cached file that has none, without
  re-outlining unchanged files or starting a language server.
- **Edits keep references current even with no language server running.** The outline of
  an edited file still needs Roslyn and stays removed until the next index or cache miss,
  but its occurrence rows are rebuilt from the text at once, so `find_occurrences` and
  `symbols references` never report stale lines for a file an agent just changed. Rows are
  keyed by path for that reason, independent of the outline entry.
- **`symbols config <term>` and `symbols packages <term>`** over a new inventory of non-code
  facts: PackageReference, ProjectReference and selected properties from `.csproj`,
  `.props` and `.targets`; flattened colon-path keys from `appsettings*.json`,
  `*settings*.json`, `*.Octodeploy.json`, `ocelot*.json`, `launchSettings.json` and
  `web.config`. Values are truncated to 120 characters and redacted when the key names a
  password, secret, token, API key, connection string or credential, since an index can be
  bundled and shared.
- **`project inventory <path>`** rebuilds only the inventory in seconds, no language server.
  `project index` builds it as well. `symbols stats` reports occurrence rows per language
  and inventory rows per kind.

## [0.5.0]

Built from a three-round comparison of agents refactoring a 25-project C# solution with
Serena, with Rider's MCP server and with grep plus the compiler. Half of the Serena
agent's builds failed on errors the language server already knew about, 371 of its
edits were single-line `replace_content` calls, and files it created or edited dropped
out of the symbol cache for the whole session because Roslyn never reached `Ready`.

### Added
- **`get_file_diagnostics`**: the language server's compiler and analyzer diagnostics for
  one file, as `severity CODE (line,col): message` lines with one-based positions. Uses
  pull diagnostics (`textDocument/diagnostic`, advertised through a new client
  capability and detected through dynamic registration) and falls back to the last
  `publishDiagnostics` for servers that only push. Starts the language server if
  needed and returns the warming response while the workspace loads.
- **`apply_edits`**: a batch of find-and-replace edits across files in one call. Edits
  to one file are applied to the running text and written atomically; a failed edit
  leaves its file untouched and is reported with its reason while other files proceed.
  The result echoes each changed line with its number and, when the file's language
  server is already running and Ready or Partial, the diagnostics for every written
  file. Tool parameters may now be arrays of objects; the schema emits `items: object`.
- **`replace_content` options `context_lines`, `report_diagnostics` and `min_severity`**
  give a single edit the same echo and check. Defaults keep the previous output.

### Changed
- **Edited and created files are reindexed inline in `Partial` as well as `Ready`.** A
  single-file document symbol request does not need the whole workspace, and on a
  solution with one unloadable project Roslyn ends `Partial` every time, so the inline
  refresh never ran and every new or edited file was a cache miss until the next
  `project index`.
- **A new file mirrors the BOM of its nearest sibling with the same extension** (own
  directory, then up to three parents) instead of always being written without one.
- **The instructions manual** tells agents to list references before changing a
  signature, batch edits with `apply_edits`, check with diagnostics instead of builds,
  trust the echoed lines instead of re-reading, and re-activate after a command-line
  `project index`.

## [0.4.0]

The symbol cache format changed (version 2): rerun `project index` once after upgrading.

### Changed
- **`symbols` queries work on an index built at another path.** A prebuilt index shipped
  in a relocatable bundle stores the build machine's absolute paths; `symbols overview`
  now translates a local path into the stored form before reporting a file as absent,
  and results are printed relative to the local root instead of as paths that do not
  exist here. `symbols files` accepts `*` and `?` globs as well as substrings.
- **`project index` requests files in parallel.** Indexing asked the language server for
  one file at a time, leaving all but one core idle. `--parallelism N` (default: half the
  logical CPUs, at most 8) sends N requests at once. Files that time out under load are
  retried one at a time with three times the per-file timeout before they are reported.
- **The symbol cache is checkpointed every 1000 files.** It was written only when the
  language server stopped, so a multi-hour index existed only in memory and a crash near
  the end lost all of it. An interrupted run now resumes from the last checkpoint.
- **Language servers are restarted every 2000 files when the repository is too large to
  load without a solution scope,** or whenever a server has died.
  Roslyn treats files outside a loaded workspace as miscellaneous documents and slows
  down with every one it has seen, so whole-tree indexing of a large repository degraded
  quadratically. Restarts skip the LSP shutdown handshake, which Roslyn routinely ignores
  until the 15-second shutdown timeout, and cost one to two seconds.
  `--restart-every N` overrides the interval; `0` disables restarts.
- **C# indexing waits until Roslyn applies build symbols to loose files.** Roslyn parses
  files outside a loaded workspace through a canonical project that takes a moment to
  load; files requested before it has are parsed with no symbols defined, so
  `#if DEBUG` and `#if NET8_0_OR_GREATER` blocks vanished from their outlines depending
  on timing. Each C# session now probes a temporary `#if DEBUG` file until its class
  appears (warning after 30 seconds) before indexing.
- **The expected "repository too large to open unscoped" warmup failure is a one-line
  warning,** logged once per language instead of an error with a stack trace on every
  start.

  Together these took a whole-tree index of a 57,000-file C# repository from about four
  hours to about ten minutes, with identical symbol output.

### Added
- **`.serenaignore`** at the project root: Serena's own ignore rules in `.gitignore`
  syntax, applied by every Serena tool and after every `.gitignore`, for trees without a
  `.gitignore` (TFVC workspaces) and for vendored or minified code.
- **C# outlines include code that only compiles for .NET Framework.** Roslyn outlines
  files outside a loaded workspace as modern .NET, so declarations inside
  `#if NETFRAMEWORK`, `#if NET48` or the `#else` of `#if NET` were inactive and missing
  from the index: whole files in a multi-targeting codebase had no symbols. For C# files
  whose conditionals test target-framework symbols, a second outline is requested with
  the .NET Framework 4.8 symbols in effect and merged with the first, so the index holds
  every declaration either build compiles. Indexing, inline cache refresh and live symbol
  retrieval all use the merged outline.

- **The symbol cache version is now 2,** so caches built before the outline changes above
  are rebuilt on the next index instead of being reused by fingerprint.
- **`project index` exits with code 1** on invalid arguments or a missing directory or
  solution, instead of 0.

### Fixed
- **Sending a changed document to Roslyn terminated the server.** `didChange` sent a
  full-document replacement as a change with a null range; Roslyn advertises incremental
  sync, dereferences the range of every change, and exited on the
  NullReferenceException. Changes now replace the previous text through an explicit
  range covering all of it. The range is computed from the text the server actually
  holds, not from the file, which an edit tool has usually rewritten by the time it
  notifies, and counts line breaks the way the server does (U+2028 and U+2029, plus
  U+0085 for C#), so the server's copy never keeps a stale tail.
- **A language server that crashes during indexing is restarted at the next batch,**
  and the timeout retry pass stops after three consecutive timeouts instead of waiting
  out every remaining file against an unresponsive server.

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
