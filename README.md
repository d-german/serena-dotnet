# Serena.DotNet

A code-aware MCP (Model Context Protocol) coding agent for **C# / .NET**, with Roslyn LSP integration. Targets .NET 10. Inspired by and indebted to [oraios/serena](https://github.com/oraios/serena).

Distributed as the global tool **`Serena.DotNet`** (executable `serena-dotnet`).

> Status: **0.1.0 — public preview.** API surface is stable enough for daily use; expect minor tool-schema tweaks before 1.0.

## Origin

I built this because I wanted the symbol-aware MCP experience of oraios/serena without the Python/`uv`/`uvx` install dance on every dev machine. On Windows-heavy .NET shops, `dotnet tool install -g` is already the established way developers ship CLI tooling — one binary, one PATH entry, predictable updates, no per-project venv. This project is a from-scratch reimplementation of *the agent-facing tool surface* in .NET, optimized for C# codebases as the primary target. It is not a fork — no Python code was translated — but the tool naming, the symbol-aware approach, the project-activation model, and the memory system all come from oraios/serena's design.

## Acknowledgements

[oraios/serena](https://github.com/oraios/serena) by Oraios AI is the original. Their design — symbol-level tools, project-based workflow, memory system, language-server backend abstraction — is the foundation this project builds on. If you work in Python, Java, Rust, Go, TypeScript, or any of the [40+ languages they support](https://github.com/oraios/serena), use theirs. They've put years of work into a polished, multi-language product. This project exists to serve a narrower audience — C# developers who already have the .NET SDK installed and want a single-binary install — and may be the right fit for that case.

## Installation

```bash
dotnet tool install -g Serena.DotNet
```

Update an existing install:

```bash
dotnet tool update -g Serena.DotNet
```

No Python, no uv, no virtualenv. If you have the .NET 10 SDK, you already have everything you need.

## How `Serena.DotNet` differs from `oraios/serena`

| Dimension | `oraios/serena` (Python) | `Serena.DotNet` (this project) |
|---|---|---|
| **Install** | `uv tool install ... serena-agent@latest --prerelease=allow` | `dotnet tool install -g Serena.DotNet` |
| **Prerequisites** | `uv` (which itself needs to be installed first) | .NET 10 SDK (most .NET devs already have it) |
| **Runtime** | Python 3.13 | .NET 10 native binary |
| **Language scope** | 40+ languages via pluggable LSP backends; also a paid JetBrains backend | C# / .NET first-class via the official Microsoft.CodeAnalysis.LanguageServer (Roslyn). Other languages possible but not the focus. |
| **Symbol cache format** | Internal | **Human-readable JSON** at `.serena/cache/<lang>/symbols.json` — you can `cat` it, grep it, diff it, or hand-edit it. Fingerprinted by file size + LastWriteTimeUtc. |
| **Cache build** | Built on demand during the session | Explicit, one-shot `serena-dotnet project index .` builds the whole repo upfront so the first `find_symbol` returns instantly with zero LSP traffic. |
| **LSP startup** | Server starts during normal flow | **Lazy** — Roslyn doesn't spin up at all if every query can be answered from the symbol cache. Saves multiple GB of RAM and minutes of warmup on idle sessions. |
| **`find_symbol` on huge results** | Returns matches with bodies; if the response is large, the MCP client decides to spill it to a sidecar file and the agent loses navigability | **Outline-first**: every match's `name_path`, `kind`, `file:line-range`, and `body_chars` is emitted inline as a header *before* the bodies. Bodies past a per-body cap are replaced with `<body omitted: N chars; re-call find_symbol with relative_path="X", name_path="Y", include_body=true to fetch>` so the agent always knows what's there and how to ask for it. |
| **Overload discovery** | Standard symbol tools | Dedicated `match_overloads: true` parameter on `find_symbol` returns every symbol with the same leaf name regardless of parent path — designed for "show me all `IsFullText(...)` overloads in one call". |
| **No-match recovery** | Returns "no symbols found" | When `find_symbol` doesn't match exactly but a leaf name does match elsewhere, returns a "did you mean" list of qualified candidates with line ranges — using the cache it already loaded, no extra LSP calls. |
| **`get_symbols_overview` on .NET files** | Returns top-level outline | Auto-descends through pure container kinds (Namespace, Module, Package). C# files almost always wrap their types in a single namespace, so depth=0 returns the actual class/method outline rather than the namespace span. Opt out with `auto_descend_containers: false`. |
| **File-write encoding** | Writes via Python `open()` defaults | **BOM-preserving**: edit tools sniff the file's first 3 bytes and emit a UTF-8 BOM only if the original had one. csproj/JSON/yaml files stay BOM-free; files that originally had a BOM keep it. New files are written without a BOM. |
| **Per-tool timeout** | Configurable | `SERENA_TOOL_TIMEOUT_SECONDS` enforces a hard ceiling; on expiry the Roslyn process tree is **force-killed** and rebuilt so a stuck LSP can't pin a CPU forever. |
| **Warming/Loading/Ready telemetry** | Tools may return empty mid-load | Pre-flight readiness gate: `find_symbol`/`find_referencing_symbols` throw a structured `language_server_warming` response with workspace state and uptime instead of pretending the symbol doesn't exist. Tool-side advice tells the agent whether to poll, scope down, or restart. |
| **Maturity / scope** | Years of work, 23k+ stars, 155+ contributors, 40+ languages, multi-backend | Single-author, C#-focused, **0.1.0**. Use oraios/serena for breadth; use this when the .NET-specific tuning matters to you. |

## CLI

```bash
serena-dotnet serve                        # Start the MCP server over stdio
serena-dotnet config show                  # Print current config
serena-dotnet config init                  # Write default ~/.serena/config.yml
serena-dotnet register <name> <path>       # Register a project
serena-dotnet list-projects                # Show registered projects
serena-dotnet setup                        # Initialize .serena/ in current dir
serena-dotnet doctor                       # Check LSP prerequisites
serena-dotnet project index .              # Build the symbol cache for this repo
serena-dotnet version                      # Show version info
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
      "cwd": "C:\\path\\to\\your\\repo",
      "env": {
        "SERENA_TOOL_TIMEOUT_SECONDS": "600",
        "SERENA_LSP_REQUEST_TIMEOUT_SECONDS": "600",
        "SERENA_LSP_PARALLELISM": "2"
      }
    }
  }
}
```

`cwd` controls which repo Serena treats as the active project. The env vars are optional but recommended for large solutions where Roslyn warmup or per-request work can exceed the lower defaults.

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

### Symbol-tool ergonomics (v1.0.34)

- `find_symbol` returns **outline-first** when `include_body=true`: a compact name-path/line-range index for every match precedes the bodies, so even when the MCP client spills the response to a sidecar file, the inline header still tells the agent where everything is. Bodies that exceed the per-body cap are replaced with a `<body omitted: N chars; re-call ... include_body=true>` hint that includes the exact arguments to fetch them.
- `find_symbol` with `match_overloads: true` returns every symbol whose leaf name matches the pattern's last segment, regardless of parent path — purpose-built for enumerating method overloads in a single call.
- When `find_symbol` finds no exact match, it auto-suggests symbols with the same leaf name as a "did you mean" list (using the cache it already loaded — no extra LSP calls).
- `get_symbols_overview` auto-descends past pure container kinds (Namespace, Module, Package). C# files almost always have a single wrapping namespace, so depth=0 now returns the actual class/method outline instead of the namespace span. Pass `auto_descend_containers: false` to opt out.

### File-write behavior

All edit tools (`replace_content`, `create_text_file`, `insert_at_line`, `replace_lines`, `delete_lines`, and the symbol-edit tools) preserve the target file's existing UTF-8 BOM state. Files without a BOM stay BOM-free; files with one keep theirs. Newly created files are written without a BOM.

## Performance on Large Repos

For repos with thousands of source files (e.g. monorepos with hundreds of projects), `find_symbol` is **cache-first by default**. Build the cache once per repo:

```bash
serena-dotnet project index .
```

After that, every `find_symbol` call:

1. Serves results from the on-disk symbol cache (no LSP traffic).
2. Runs a debounced parallel fingerprint check on cached files.
3. Auto-reindexes up to 10 stale files inline so subsequent calls stay fresh.

If more than 10 cached files have changed since the last index (e.g. after a `git pull`), the refresher logs a warning and skips inline reindex — re-run `serena-dotnet project index .` to refresh the bulk.

The cache lives under `.serena/cache/<lang>/symbols.json` in the repo and is fingerprinted by file size + LastWriteTimeUtc.

### Tunable env vars

| Variable | Default | Range | Purpose |
|---|---|---|---|
| `SERENA_TOOL_TIMEOUT_SECONDS` | 90 | 5–3600 | Per-tool-call timeout (Roslyn force-restart on expiry) |
| `SERENA_LSP_REQUEST_TIMEOUT_SECONDS` | 60 | 5–1800 | Per-LSP-request timeout (raise for very slow uncached requests on huge solutions) |
| `SERENA_LSP_PARALLELISM` | 2 | 1–16 | Concurrent `documentSymbol` requests (higher floods Roslyn) |
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
