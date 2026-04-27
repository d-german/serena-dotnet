# Serena.DotNet

A code-aware MCP (Model Context Protocol) coding agent for .NET, with Roslyn LSP integration. Targets .NET 10.

Distributed as the global tool **`Serena.DotNet`** (executable `serena-dotnet`).

## Installation

```bash
dotnet tool install -g Serena.DotNet
```

Update an existing install:

```bash
dotnet tool update -g Serena.DotNet
```

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
