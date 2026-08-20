# Serena.DotNet

A code-aware MCP (Model Context Protocol) coding agent for **.NET-first code search and editing**, with Roslyn LSP integration for C# and cache-backed support for additional language servers. Targets .NET 10. Inspired by and indebted to [oraios/serena](https://github.com/oraios/serena).

Distributed as the global tool **`Serena.DotNet`** (executable `serena-dotnet`).

> Status: **0.1.5 - public preview.** API surface is stable enough for daily use; expect minor tool-schema tweaks before 1.0.

## Origin

I built this because I wanted the symbol-aware MCP experience of oraios/serena without the Python/`uv`/`uvx` install dance on every dev machine. On Windows-heavy .NET shops, `dotnet tool install -g` is already the established way developers ship CLI tooling - one binary, one PATH entry, predictable updates, no per-project venv. This project is a from-scratch reimplementation of *the agent-facing tool surface* in .NET, optimized for C# codebases as the primary target. It is not a fork - no Python code was translated - but the tool naming, the symbol-aware approach, the project-activation model, and the memory system all come from oraios/serena's design.

## Acknowledgements

[oraios/serena](https://github.com/oraios/serena) by Oraios AI is the original. Their design - symbol-level tools, project-based workflow, memory system, language-server backend abstraction - is the foundation this project builds on. If you need the broadest and most mature multi-language Serena implementation, use theirs. They've put years of work into a polished product. This project exists to serve the narrower case where a .NET global tool, Windows-friendly install, and C#/Roslyn tuning matter most, while still exposing language-server hooks for other languages.

## Installation

```bash
dotnet tool install -g Serena.DotNet
```

Update an existing install:

```bash
dotnet tool update -g Serena.DotNet
```

For the core tool and C#/Roslyn path: no Python, no uv, no virtualenv. Other language servers may require their normal runtime, such as Node.js for TypeScript/JavaScript tooling.

## How `Serena.DotNet` differs from `oraios/serena`

| Dimension | `oraios/serena` (Python) | `Serena.DotNet` (this project) |
|---|---|---|
| **Install** | `uv tool install ... serena-agent@latest --prerelease=allow` | `dotnet tool install -g Serena.DotNet` |
| **Prerequisites** | `uv` (which itself needs to be installed first) | .NET 10 SDK (most .NET devs already have it) |
| **Runtime** | Python 3.13 | .NET 10 native binary |
| **Language scope** | 40+ languages via pluggable LSP backends; also a paid JetBrains backend | C# / .NET first-class via the official Microsoft.CodeAnalysis.LanguageServer (Roslyn). TypeScript/JavaScript, Python, Rust, Go, and several additional LSP backends can also be enabled per project. |
| **Symbol cache format** | Internal | Indexed SQLite at `.serena/cache/<lang>/symbols.db`. Entries are loaded on demand and declaration names have their own index, so a query does not deserialize or scan a monolithic cache. Existing `symbols.json` caches migrate automatically. |
| **Cache build** | Built on demand during the session | `serena-dotnet project index .` builds the whole repo; `project index <repo-root> --solution <sln>` indexes that solution's projects, resolvable transitive `ProjectReference` dependencies, and linked sources into the repo-level cache. |
| **LSP startup** | Server starts during normal flow | **Lazy** — Roslyn doesn't spin up at all if every query can be answered from the symbol cache. Saves multiple GB of RAM and minutes of warmup on idle sessions. |
| **`find_symbol` on huge results** | Returns matches with bodies; if the response is large, the MCP client decides to spill it to a sidecar file and the agent loses navigability | **Outline-first**: every match's `name_path`, `kind`, `file:line-range`, and `body_chars` is emitted inline as a header *before* the bodies. Bodies past a per-body cap are replaced with `<body omitted: N chars; re-call find_symbol with relative_path="X", name_path="Y", include_body=true to fetch>` so the agent always knows what's there and how to ask for it. |
| **Overload discovery** | Standard symbol tools | Dedicated `match_overloads: true` parameter on `find_symbol` returns every symbol with the same leaf name regardless of parent path — designed for "show me all `IsFullText(...)` overloads in one call". |
| **No-match recovery** | Returns "no symbols found" | When `find_symbol` doesn't match exactly but a leaf name does match elsewhere, returns a "did you mean" list of qualified candidates with line ranges — using the cache it already loaded, no extra LSP calls. |
| **`get_symbols_overview` on .NET files** | Returns top-level outline | Auto-descends through pure container kinds (Namespace, Module, Package). C# files almost always wrap their types in a single namespace, so depth=0 returns the actual class/method outline rather than the namespace span. Opt out with `auto_descend_containers: false`. |
| **File-write encoding** | Writes via Python `open()` defaults | **BOM-preserving**: edit tools sniff the file's first 3 bytes and emit a UTF-8 BOM only if the original had one. csproj/JSON/yaml files stay BOM-free; files that originally had a BOM keep it. New files are written without a BOM. |
| **Per-tool timeout** | Configurable | `SERENA_TOOL_TIMEOUT_SECONDS` enforces a hard ceiling; on expiry the Roslyn process tree is **force-killed** and rebuilt so a stuck LSP can't pin a CPU forever. |
| **Workspace telemetry** | Tools may return empty mid-load | Explicit `NotStarted`, `Loading`, `Ready`, `Partial`, and `Failed` states with solution project counts and Roslyn project-load warnings. A timed-out or warning-bearing workspace is never silently reported as complete. |
| **Maturity / scope** | Years of work, 23k+ stars, 155+ contributors, 40+ languages, multi-backend | Single-author, .NET-first, **0.1.5**. Use oraios/serena for breadth and maturity; use this when the .NET install path, cache behavior, or Roslyn tuning matters to you. |

## CLI

```bash
serena-dotnet serve                        # Start the MCP server over stdio
serena-dotnet config show                  # Print current config
serena-dotnet config init                  # Write default ~/.serena/config.yml
serena-dotnet register <path> --name <n>   # Register a project
serena-dotnet list-projects                # Show registered projects
serena-dotnet setup                        # Initialize .serena/project.yml in current dir
serena-dotnet doctor                       # Check LSP prerequisites
serena-dotnet project index .              # Build the symbol cache for this repo
serena-dotnet project index C:\repo --solution Product.sln  # Index one solution into the repo cache and save its Roslyn scope
serena-dotnet project index-file <file>    # Refresh one file in the symbol cache
serena-dotnet version                      # Show version info
```

`serena-dotnet setup` writes `.serena/project.yml`. For C# repos it defaults to:

```yaml
languages:
  - csharp
```

That keeps the project scoped to Roslyn and avoids starting unrelated language servers. Add only the languages you want, for example:

```yaml
languages:
  - csharp
  - typescript
  - python
```

Removing the `languages` block enables extension-based auto-detection. An explicit list is recommended: the file-extension detector recognizes more languages than currently have registered language-server backends.

To use a server outside `PATH`, configure it per language:

```yaml
languages:
  - csharp
  - python
ls_specific_settings:
  python:
    server_path: C:\tools\pyright-langserver.cmd
```

## MCP Client Configuration

Minimal stdio entry for VS Code (`.vscode/mcp.json`) or any MCP client:

```jsonc
{
  "servers": {
    "serena-dotnet": {
      "type": "stdio",
      "command": "serena-dotnet",
      "args": ["serve"],
      "env": {
        "SystemRoot": "C:\\Windows",
        "SERENA_TOOL_TIMEOUT_SECONDS": "600",
        "SERENA_LSP_REQUEST_TIMEOUT_SECONDS": "600",
        "SERENA_LSP_PARALLELISM": "2",
        "SERENA_LSP_SHUTDOWN_TIMEOUT_SECONDS": "15"
      }
    }
  }
}
```

Most MCP clients launch stdio servers with the workspace folder as the working directory, but activation is explicit. Agents should call `activate_project` for the repo they want to inspect. For a fixed server tied to one repo, pass `--project`:

```jsonc
"args": ["serve", "--project", "C:\\path\\to\\repo"]
```

The env vars are optional but recommended for large solutions where Roslyn warmup, shutdown, or per-request work can exceed the lower defaults. `SystemRoot` is useful for Windows hosts that start MCP servers with a stripped environment.

## Language Support

C# is the primary supported path, but Serena.DotNet has 13 registered language-server backends. Language-server installation is lazy: Serena looks for or installs a server when that language is first needed, not when Serena.DotNet itself is installed and not merely because the MCP server started.

| Language | Executable | How Serena finds or installs it |
|---|---|---|
| C# | `roslyn-language-server` | Checks `~/.dotnet/tools` and `PATH`. If missing, runs `dotnet tool install -g roslyn-language-server --prerelease`. Falls back to `csharp-ls` from `PATH`. |
| TypeScript / JavaScript | `typescript-language-server` | Auto-installs the `typescript-language-server` and `typescript` npm packages under `~/.serena-dotnet/language-servers/typescript`, then falls back to `PATH`. Requires Node.js and npm. |
| Python | `pyright-langserver` | Auto-installs the `pyright` npm package under `~/.serena-dotnet/language-servers/python`, then falls back to `PATH`. Requires Node.js and npm. |
| Rust | `rust-analyzer` | Must already be available in `PATH`. |
| Go | `gopls` | Must already be available in `PATH`. |
| Java | `jdtls` | Must already be available in `PATH`. |
| Kotlin | `kotlin-language-server` | Must already be available in `PATH`. |
| C / C++ | `clangd` | Must already be available in `PATH`. |
| Ruby | `solargraph` | Must already be available in `PATH`. |
| PHP | `phpactor` | Must already be available in `PATH`. |
| Bash | `bash-language-server` | Must already be available in `PATH`. |
| Dart | `dart` | Must already be available in `PATH`. |
| Elixir | `elixir-ls` | Must already be available in `PATH`. |

Common manual installs:

```bash
dotnet tool install -g roslyn-language-server --prerelease
npm install -g typescript-language-server typescript
npm install -g pyright
rustup component add rust-analyzer
go install golang.org/x/tools/gopls@latest
gem install solargraph
npm install -g bash-language-server
```

For the remaining backends, install the server from its project and make the executable in the table visible in `PATH`: [Eclipse JDT LS](https://github.com/eclipse-jdtls/eclipse.jdt.ls), [kotlin-language-server](https://github.com/fwcd/kotlin-language-server), [clangd](https://clangd.llvm.org/installation.html), [Phpactor](https://phpactor.readthedocs.io/en/master/usage/standalone.html), [Dart SDK](https://dart.dev/get-dart), or [ElixirLS](https://github.com/elixir-lsp/elixir-ls). If a binary is installed elsewhere, use `ls_specific_settings.<language>.server_path` as shown above.

`serena-dotnet doctor` checks all 13 backends. For TypeScript and Python, `(local)` means Serena installed the npm packages in the directories above; `(global)` means the executable came from `PATH`; `(dotnet tool)` identifies a .NET global tool. Missing-server lines include the install command or next step. Roslyn, TypeScript, and Pyright are installed lazily on the first request for that language, which is why they may appear even if you do not remember installing them manually.

For production use, scope `.serena/project.yml` to the languages you actually want. This avoids accidental language-server startup in mixed repositories.

## Tools (38)

| Category | Tools |
|---|---|
| **File** | `read_file`, `create_text_file`, `list_dir`, `find_file`, `search_for_pattern` |
| **Symbol (read)** | `find_symbol`, `get_symbols_overview`, `find_referencing_symbols` |
| **Symbol (edit)** | `replace_symbol_body`, `insert_before_symbol`, `insert_after_symbol`, `rename_symbol`, `safe_delete_symbol` |
| **Line edit** | `insert_at_line`, `replace_lines`, `delete_lines`, `replace_content` |
| **Memory** | `read_memory`, `write_memory`, `list_memories`, `delete_memory`, `rename_memory`, `edit_memory` |
| **Project** | `activate_project`, `get_current_config`, `set_active_solution`, `clear_active_solution`, `remove_project`, `list_queryable_projects`, `query_project` |
| **Language server** | `warm_language_server`, `get_language_server_status`, `restart_language_server`, `kill_language_server` |
| **Workflow** | `check_onboarding_performed`, `onboarding`, `initial_instructions`, `execute_shell_command` |

### Symbol-tool ergonomics

- `find_symbol` returns **outline-first** when `include_body=true`: a compact name-path/line-range index for every match precedes the bodies, so even when the MCP client spills the response to a sidecar file, the inline header still tells the agent where everything is. Bodies that exceed the per-body cap are replaced with a `<body omitted: N chars; re-call ... include_body=true>` hint that includes the exact arguments to fetch them.
- `find_symbol` with `match_overloads: true` returns every symbol whose leaf name matches the pattern's last segment, regardless of parent path — purpose-built for enumerating method overloads in a single call.
- When `find_symbol` finds no exact match, it auto-suggests symbols with the same leaf name as a "did you mean" list (using the cache it already loaded — no extra LSP calls).
- `get_symbols_overview` auto-descends past pure container kinds (Namespace, Module, Package). C# files almost always have a single wrapping namespace, so depth=0 now returns the actual class/method outline instead of the namespace span. Pass `auto_descend_containers: false` to opt out.

### File-write behavior

All edit tools (`replace_content`, `create_text_file`, `insert_at_line`, `replace_lines`, `delete_lines`, and the symbol-edit tools) preserve the target file's existing UTF-8 BOM state. Files without a BOM stay BOM-free; files with one keep theirs. Newly created files are written without a BOM.

After every successful write, Serena removes that file's symbol-cache entry and records the invalidation in `.serena/cache/<lang>/invalidated-paths.txt`. Plain file/text writes do **not** cold-start the language server. If the matching language server is already running and Ready, Serena refreshes just that one file; otherwise the stale entry stays removed until the next index or semantic lookup can rebuild it. This prevents old symbols from leaking through cache-backed project-wide searches after edits.

## Performance on Large Repos

### Work before starting Roslyn

Activating a project does **not** start a language server. The following file and text tools can always be used immediately without opening a solution or loading the Roslyn workspace:

- `list_dir`, `find_file`, `read_file`, and `search_for_pattern`

Symbol tools are **cache-first**. When `.serena/cache/csharp/symbols.db` contains the requested files:

- `get_symbols_overview` returns the cached document-symbol tree without starting Roslyn
- `find_symbol` searches cached symbols without starting Roslyn, including file-scoped searches using `relative_path`

On a true cache miss, those symbol tools start the language server to request document symbols. Use `get_language_server_status` when it matters whether Roslyn was started.

This is often enough to answer structural questions about a large repository. Prefer `find_symbol` for declarations and `get_symbols_overview` for a file's type/member structure. Use `search_for_pattern` for arbitrary text, configuration keys, UI strings, or as a fallback when you do not yet know a symbol name.

### Why this is more than grep

`search_for_pattern` is intentionally a text scan; that tool alone is not Serena's value. The cache-backed symbol tools return parsed declarations with symbol kind, qualified `name_path`, parent/child structure, exact body range, and optional body text. They avoid comment/string false positives and use the SQLite declaration-name index to open only candidate files. A solution-scoped index also retains the repository root while following every C# project listed in the solution plus resolvable transitive `ProjectReference` dependencies, including projects outside the application's directory. When Roslyn is needed, `find_referencing_symbols` returns resolved symbol relationships rather than identical text and reports whether workspace completeness is guaranteed.

Roslyn is required for solution-wide symbol queries without a usable cache, `find_referencing_symbols`, `rename_symbol`, and symbol-targeted edits. On a large multi-solution repository, scope Roslyn **before** calling `warm_language_server` or a Roslyn-bound tool. Serena refuses to open an unbounded workspace when it finds multiple solutions or more than 50 unscoped C# projects; `get_language_server_status` reports the failure reason and tells you to select a solution.

A representative Patient Window validation on a warm filesystem illustrates the intended tradeoff (timings are machine-dependent):

| Operation | Time | Output | What it found |
|---|---:|---:|---|
| Cache-backed exact `find_symbol("DocumentCorrectionActionModel")` | 78 ms first call, 6 ms repeat | 532 characters | The structured declaration in a dependency project outside the Patient Window application directory; C# remained `NotStarted`. |
| `rg` limited to the Patient Window application directory | 195 ms | 1,473 characters / 7 lines | Fast text matches, but it missed that dependency declaration. |
| `rg` over the repository | 71.5 s | 3,592 characters / 18 lines | Found all textual occurrences, without declaration structure or symbol identity. |
| Roslyn `find_referencing_symbols` over the 29-project solution | 3.2 s after a 65 s cold warmup | 11 resolved references in 2 files | Semantic references, marked `Partial` / `not_guaranteed` because the legacy solution reported unresolved dependencies. |

This is the deployment test: cached declaration discovery should beat a repository scan and return structure; Roslyn should be reserved for relationships that text search cannot prove. If a repository has no usable cache and no need for semantic relationships, Serena's text-search path is not inherently more valuable than grep.

Recommended workflow:

1. Call `activate_project` for the repository. Roslyn remains stopped.
2. Use cached `find_symbol` / `get_symbols_overview` first when you know the declaration or file. Use `search_for_pattern`, `find_file`, and `read_file` for non-symbol discovery.
3. If the relevant solution has not been indexed, run `serena-dotnet project index <repo-root> --solution <solution>` once. This also persists the C# scope.
4. If the cache misses or cross-file semantic analysis is needed, call `set_active_solution` with only the relevant solution:

   ```json
   {
     "solution_path": "src/Product/Product.sln"
   }
   ```

5. Call `warm_language_server`, then poll `get_language_server_status`. `Ready` means initialization completed without captured warnings. `Partial` permits read queries but means cross-project completeness is not guaranteed; inspect its bounded, de-duplicated `warnings` before trusting a negative result. `rename_symbol` and `safe_delete_symbol` are blocked in `Partial` because an incomplete workspace cannot prove a global edit is safe. Reference and blocked-edit responses repeat only three short warning samples and point back to the status tool, rather than spending thousands of response characters duplicating diagnostics.

`set_active_solution` persists the selection to `.serena/project.yml` under `csharp.scope.solutions`. If Roslyn is already running it is restarted; if it is stopped, the next semantic request or explicit warmup loads only the selected solution. Use `solution_paths` when a task genuinely spans multiple solutions.

### Whole-repository symbol cache

For repos with thousands of source files (e.g. monorepos with hundreds of projects), `find_symbol` is **cache-first by default**. Build the cache once per repo:

```bash
serena-dotnet project index .
```

For a focused working set in a monorepo, keep the positional path at the repository root and add a solution:

```bash
serena-dotnet project index C:\BigRepo --solution C:\BigRepo\src\MyApp.Web.sln
```

This parses the solution, follows resolvable transitive `<ProjectReference Include="...">` entries, indexes supported source files under those project directories plus explicitly linked source files, writes them into `C:\BigRepo\.serena\cache`, and persists the solution under `csharp.scope.solutions`. It does not index unrelated project trees. `ProjectReference` paths containing unevaluated MSBuild properties or wildcards require Roslyn and are not added to the cache-only closure. Re-run without `--solution` only when you actually want a cross-solution repository cache. Passing only one project directory as the positional path is different: it makes that directory a separate project root and cannot discover dependency projects elsewhere in the repository.

Repeat indexing is fingerprint-first. If every selected file is already current, the indexer reuses the cache and does not start Roslyn or any other language server. If only a few files changed, it starts only the affected language server and requests symbols only for those files.

After that, every `find_symbol` call:

1. Serves results from the on-disk symbol cache (no LSP traffic).
2. Runs a debounced parallel fingerprint check on cached files.
3. Auto-reindexes up to 10 stale files inline so subsequent calls stay fresh.
4. Ignores files listed in the write-time invalidation journal until they are refreshed.

If more than 10 cached files have changed since the last index (e.g. after a `git pull`), the refresher logs a warning and skips inline reindex — re-run the same focused `project index ... --solution ...` command to refresh that working set, or `project index .` only when a whole-repository refresh is intentional.

The cache lives under `.serena/cache/<lang>/symbols.db` and is fingerprinted by file size + LastWriteTimeUtc. Each file is stored independently and declaration leaf names are indexed by a compact numeric entry ID, so exact and substring symbol searches read only candidate entries without repeating long repository paths for every symbol. Writes update changed entries transactionally instead of rewriting the entire cache. Write invalidations live beside it in `.serena/cache/<lang>/invalidated-paths.txt`, so stale entries are suppressed even after a process restart. Legacy `symbols.json` and path-indexed SQLite caches migrate automatically.

`search_for_pattern` remains available for arbitrary regex. It prunes ignored directories before descending, scans files in parallel, merges overlapping context blocks so lines are not repeated, and caps output at 100,000 characters by default. Narrow with `relative_path` and `paths_include_glob` when possible.

Indexer discovery prunes ignored directories while walking the tree. Built-in skips include `.git`, `.serena`, `.vs`, `.idea`, `bin`, `obj`, and `node_modules`, and project `.gitignore` rules still apply. Unknown or extensionless files are not treated as C#.

### Tunable env vars

| Variable | Default | Range | Purpose |
|---|---|---|---|
| `SERENA_TOOL_TIMEOUT_SECONDS` | 90 | 5–3600 | Per-tool-call timeout (Roslyn force-restart on expiry) |
| `SERENA_LSP_REQUEST_TIMEOUT_SECONDS` | 60 | 5–1800 | Per-LSP-request timeout (raise for very slow uncached requests on huge solutions) |
| `SERENA_LSP_PARALLELISM` | 2 | 1–16 | Concurrent `documentSymbol` requests (higher floods Roslyn) |
| `SERENA_LSP_SHUTDOWN_TIMEOUT_SECONDS` | 15 | 1–120 | Grace period for language-server shutdown before the process tree is killed |
| `SERENA_SEARCH_PARALLELISM` | min(CPU, 16) | 1–32 | Parallel file scan in `search_for_pattern` |
| `SERENA_MAX_INLINE_BODY_BYTES` | 8192 | 512–1048576 | Per-body inline cap for `find_symbol include_body=true` outline-first response |

## Building from source

```bash
dotnet build
dotnet test
dotnet pack -c Release src/Serena.Cli/Serena.Cli.csproj -o nupkg
dotnet tool update -g Serena.DotNet --add-source ./nupkg
```

See [BUILD.md](BUILD.md) for additional notes.

## License

MIT
