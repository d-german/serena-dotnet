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

## License

MIT
