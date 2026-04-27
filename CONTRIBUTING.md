# Contributing to Serena.DotNet

Thanks for considering a contribution. This project is small and opinionated; the easiest way to be effective is to read [README.md](README.md) and [CHANGELOG.md](CHANGELOG.md) first, then open an issue to discuss the change before sending a PR.

## Development setup

```bash
git clone https://github.com/d-german/serena-dotnet.git
cd serena-dotnet
dotnet build
dotnet test
```

Requires the **.NET 10 SDK** (preview channel until .NET 10 GA).

## Test discipline

- All PRs must keep `dotnet test` green.
- New behavior needs a regression test. Look at existing tests under `tests/Serena.Core.Tests/` for patterns.
- Avoid changes that lower test count.

## Code conventions

- Follow the existing SOLID + Railway-Oriented patterns (`Result<T>` from `CSharpFunctionalExtensions` for fallible operations).
- Target cyclomatic complexity ≤ 5 per method; refactor into helpers when it climbs.
- Don't add error handling for scenarios that can't happen — validate at boundaries only.
- File edits: never use raw `File.WriteAllText`; route through `Serena.Core.Editor.FileWriteGate.WriteAllTextAsync` so BOM preservation and write-serialization stay consistent.
- Tools: derive from `ToolBase`, declare parameters with the PascalCase `ToolParameter` record, and apply attributes (`[CanEdit]`, `[NoActiveProjectRequired]`, `[OptionalTool]`, `[SymbolicRead]`) where appropriate.
- Read-only/symbol tools must call `RequireReadOnlyRetrieverAsync` so the cache-first lazy-LSP fast path applies.

## Releasing (maintainer-only)

1. Bump `<Version>` in `src/Serena.Cli/Serena.Cli.csproj`.
2. Update [CHANGELOG.md](CHANGELOG.md).
3. Commit + tag: `git tag vX.Y.Z && git push --tags`.
4. The `release.yml` workflow packs and pushes to nuget.org using the `NUGET_API_KEY` secret.

## Reporting issues

Include:
- `serena-dotnet --version`
- The MCP client and its version
- The relevant slice of stderr (Serena logs to stderr; stdout is reserved for JSON-RPC)
- Repro steps or, ideally, a minimal failing repo
