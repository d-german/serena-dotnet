# Serena .NET — Build & Publish Scripts

## Quick Build
```bash
dotnet build Serena.slnx
```

## Run Tests
```bash
dotnet test Serena.slnx
```

## Publish Single-File Executable

### Windows (x64)
```bash
dotnet publish src/Serena.Cli/Serena.Cli.csproj -c Release -r win-x64
```

### Linux (x64)
```bash
dotnet publish src/Serena.Cli/Serena.Cli.csproj -c Release -r linux-x64
```

### macOS (Apple Silicon)
```bash
dotnet publish src/Serena.Cli/Serena.Cli.csproj -c Release -r osx-arm64
```

### macOS (Intel)
```bash
dotnet publish src/Serena.Cli/Serena.Cli.csproj -c Release -r osx-x64
```

The output is a single self-contained executable at:
```
src/Serena.Cli/bin/Release/net10.0/<rid>/publish/serena[.exe]
```

## MCP Client Configuration

### Claude Desktop (claude_desktop_config.json)
```json
{
  "mcpServers": {
    "serena": {
      "command": "/path/to/serena",
      "args": ["serve"]
    }
  }
}
```

### GitHub Copilot (settings.json)
```json
{
  "mcp": {
    "servers": {
      "serena": {
        "command": "/path/to/serena",
        "args": ["serve"]
      }
    }
  }
}
```

## Prerequisites
- .NET 10 SDK (for building only — published binary is self-contained)
- Language servers for target languages (e.g., pyright, typescript-language-server, rust-analyzer, gopls, csharp-ls)
