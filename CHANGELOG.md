# Changelog

All notable changes to **Serena.DotNet** are recorded here. Format loosely follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [0.1.0] — Public preview

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
