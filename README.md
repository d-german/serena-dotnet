# Serena (.NET)

A code-aware MCP (Model Context Protocol) coding agent with LSP integration, ported to .NET 10.

## Installation

```bash
dotnet tool install -g Serena
```

## Usage

### Start the MCP server
```bash
serena serve --project my-project
```

### Register a project
```bash
serena register my-project C:\path\to\project
```

### List registered projects
```bash
serena list-projects
```

## MCP Client Configuration

```json
{
  "serena": {
    "type": "stdio",
    "command": "serena",
    "args": ["serve", "--project", "my-project"]
  }
}
```

## Tools (26)

| Category | Tools |
|----------|-------|
| **File** | read_file, create_text_file, list_dir, find_file, search_for_pattern |
| **Symbol** | find_symbol, get_symbols_overview, find_referencing_symbols |
| **Edit** | replace_symbol_body, insert_before_symbol, insert_after_symbol, replace_content, rename_symbol, safe_delete_symbol |
| **Memory** | read_memory, write_memory, list_memories, delete_memory, rename_memory, edit_memory |
| **Config** | activate_project, get_current_config, execute_shell_command |
| **Workflow** | check_onboarding_performed, onboarding, initial_instructions |

## Performance on Large Repos

For repos with thousands of source files (e.g. monorepos with hundreds of projects),
`find_symbol` is **cache-first by default**. The cache is built by running:

```bash
serena-dotnet project index .
```

This is a one-time cost per repo. After that, every `find_symbol` call:

1. Serves results from the on-disk symbol cache (no LSP traffic).
2. Runs a debounced parallel fingerprint check on the cached files.
3. Auto-reindexes up to 10 stale files inline so subsequent calls stay fresh.

If more than 10 cached files have changed since the last index (e.g. after a
`git pull`), the refresher logs a warning and skips inline reindex — re-run
`serena-dotnet project index .` to refresh the bulk.

There is intentionally no env-var to disable this; it is the default behavior.

### Tunable env vars

| Variable | Default | Purpose |
|----------|---------|---------|
| `SERENA_TOOL_TIMEOUT_SECONDS` | 90 | Per-tool-call timeout (clamped 5-3600) |
| `SERENA_LSP_REQUEST_TIMEOUT_SECONDS` | 300 | Per-LSP-request timeout (clamped 10-3600) |
| `SERENA_LSP_PARALLELISM` | 2 | Concurrent document-symbol requests (clamped 1-16) |
| `SERENA_SEARCH_PARALLELISM` | min(CPU, 16) | Parallel file scan in `search_for_pattern` (clamped 1-32) |

## License

MIT
