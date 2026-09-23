# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

`csmesh` is a .NET 10 CLI that builds a Roslyn symbol graph of a C# codebase and answers structural
questions about it (call paths, DI-bound implementations, blast radius, entrypoints) under a hard
token budget. It ships as a global tool, via npm, and as a Native AOT binary, and is consumed both
from a shell and as an MCP server by coding agents.

**This repo is the tool itself, and it dogfoods:** `.csmesh/` holds an index of this repository's own
source, and `.mcp.json` registers the csmesh MCP server. The MCP server binary in `.mcp.json` is the
**Debug build** (`src/CsMesh/bin/Debug/net10.0/csmesh.exe`) — after changing csmesh, rebuild before
MCP answers reflect the change, and re-index this repo (`csmesh index`) after structural edits, or the
tool answers stale about itself.

Per the global csmesh directive, prefer the `csmesh_*` MCP tools over grep/glob for anything
structural in `.cs` files. Grep remains correct for string literals, error messages, and the
non-`.cs` prose files that several tests pin.

## Build, test, run

Requires the .NET 10 SDK (`net10.0`). There is no `global.json` pin in this repo.

```bash
dotnet build CsMesh.slnx -c Release --no-restore        # CI shape
dotnet test src/tests/CsMesh.Tests/CsMesh.Tests.csproj -c Release --no-build \
  --logger "console;verbosity=normal"                   # CI shape

# one test / one class
dotnet test src/tests/CsMesh.Tests/CsMesh.Tests.csproj --filter "FullyQualifiedName~GoldenGraphTests"
dotnet test src/tests/CsMesh.Tests/CsMesh.Tests.csproj --filter "DisplayName~The_pointer_package"

dotnet run --project src/CsMesh -- map                  # run the CLI from source
dotnet pack -c Release                                  # the tool package
dotnet tool install --global --add-source ./src/CsMesh/bin/Release CsMesh
```

The shipped shape is Native AOT, built per-RID in `release.yml`:

```bash
dotnet publish src/CsMesh/CsMesh.csproj -c Release -r win-x64 -p:PublishAot=true -o publish-out
```

## Architecture

### Indexing pipeline

`Analysis/Indexer.cs` (a `partial` static class spanning `Indexer.cs`, `Indexer.Compilations.cs`,
`Indexer.Incremental.cs`) is the core. `Indexer.Build` runs:

1. **Scope** — `Analysis/ProjectScope.cs` decides which projects are part of the build by reading
   csproj/solution XML *by hand*, never by evaluating MSBuild (that would cost the startup time and
   AOT compatibility the tool exists for). A project not listed in `CsMesh.slnx` is out of scope
   unless `index --all`. Adding a project to this repo therefore means adding it to `CsMesh.slnx`.
2. **Collect trees** — source `.cs`, generated Razor output (recovered from `.razor` paths so
   components point at openable source), non-Razor generated sources from `obj/**/generated`, and
   synthesized global usings. `bin/`, `obj/`, `.g.cs`, `.Designer.cs` are skipped on the normal walk
   and re-added only through their own paths.
3. **References** — the runtime directory plus the working tree's `bin/` DLLs, with managed assemblies
   identified by reading the PE header (`IsManagedAssembly`).
4. **Compile** — **one `CSharpCompilation` per in-scope project** in topological `ProjectReference`
   order, each with a `CompilationReference` to every project in its transitive closure. This is
   deliberate and load-bearing; see `docs/adr/0002-per-project-compilation.md`. A single
   whole-solution compilation silently leaked names, global usings and assembly boundaries across
   projects.
5. **Three passes** over the bindings, in `Builder`: `Pass1_Declarations` (nodes), `Pass2_Bodies`
   (call edges, DI registrations, mediator dispatch, routes, member-access roles),
   `Pass3_Indirection` (interface/override edges — deferred because the `di-bound` note depends on
   pass 2's container registrations).

Incremental refresh (`Indexer.Incremental.cs`) re-binds only edited files and recreates their symbols
under their **original node ids**, and falls back to a full pass for anything that binds across files.

### Graph model and persistence

- `Node.Key` is the compiler-derived identity (container, name, arity, parameter types) and is the
  only stable anchor — `Id` is a persisted int from `Graph.NextNodeId`. Never key or compare by
  positional index; that is exactly what the `Key` field was introduced to end.
- Edges carry `EdgeKind` (`Call`, `Interface`, `Override`, `Mediatr`, `DiBinding`, `Construct`,
  `TypeUse`, `Route`), an `EdgeRole` (`Read`/`Write`/`Subscribe`, selected by `--writes`), a
  confidence, and the wiring site.
- `Graph.CurrentFormatVersion` is bumped on any change to node keying, edge semantics or on-disk
  shape; a graph from an older version is **rejected** on load rather than half-read.
  `Graph.BuiltByVersion` catches the more common case of an upgraded binary reading a well-formed
  graph and missing detections that release added.
- `Storage/GraphStore.cs` serializes to a temp file and renames it into place under a lock file.
  Contention is reported as exit `75` (retryable), never a crash. The `.csmesh/` directory is created
  only through `Storage/CsMeshDir.cs`, which also adds it to `.git/info/exclude`.
- Graph freshness is tracked with per-file and per-directory stamps, so an incremental pass knows
  what moved without rescanning.

### The answer path

`Program.cs` → `CliRunner.RunGuarded` → the command switch → `Commands/*` → `Analysis/Queries.*`
(one file per command family) → `Common/BudgetWriter`.

- **Every command returns an `int` from `Common/ExitCodes.cs`.** Those codes are the public contract;
  agents branch on them instead of parsing prose. `RunGuarded` separates an internal fault (`70`,
  and `75` for storage contention) from a usage error (`64`) so a crash is never mistaken for a bad
  command line.
- **All answer text goes through `BudgetWriter`**, which enforces a hard token cap, marks
  `Overflowed` (→ exit `2`), reserves tokens for notes and the completion marker, and keeps the rows
  so `--json` and the text rendering describe the same answer. New output that bypasses the writer
  breaks the budget contract.
- JSON is serialized through the source-generated `Common/AppJsonContext.cs`; DTOs live in
  `Models/Reports.cs` and `Models/QueryResult.cs`. The Release pipeline publishes Native AOT, so
  reflection-based serialization or runtime code generation would break the shipped binary.
- `Commands/QueryCommand.cs` is the shared entry for the read-only commands; it also handles
  `--heal` / `CSMESH_AUTO_INDEX` in-place healing and the staleness and version-gap notes.

### MCP server

`Mcp/McpServer.cs` + `Mcp/McpTools.cs`. The catalogue translates a tool call into the same CLI
command the shell would run and captures what it printed — deliberately no second implementation, so
an MCP answer and a terminal answer cannot drift. Adding a query means adding a command; the MCP tool
then just forwards to it.

### Skill and agent integration

`Skill/SkillText.cs` holds the rules text as the single source. `Commands/SkillCommand.cs` writes it
as `SKILL.md` plus fenced `<!-- csmesh-instructions -->` blocks into `AGENTS.md`,
`.cursor/rules/csmesh.mdc`, `.clinerules`, `GEMINI.md` and `.github/copilot-instructions.md`, and
`Common/AgentIntegration.cs` (un)registers the MCP server entries per agent and per scope.

- Never hand-edit inside the `<!-- csmesh-instructions -->` markers — that block is generated.
- `AGENTS.md` and `SKILL.md` are tracked. The other generated agent files are gitignored: run
  `csmesh install` to regenerate them rather than editing them.

## Conventions enforced by tests

Several prose files are compile-free contracts; the suite reads them and compares them to the code.
Changing behaviour means updating the prose in the same commit, or these go red:

| contract | test |
|---|---|
| root `SKILL.md` == `SkillText.Markdown` | `SkillTextTests` |
| exit tables and default-budget prose in `README.md` == the emitted codes and real caps | `DocumentedContractTests`, `HelpTextTests` |
| files carrying an installed block == `SkillCommand.BlockTargets` | `InstallBlockTargetsTests` |
| `npm/package.json` version == `CsMesh.csproj` `Version` | `PackageVersionTests` |
| tool-package RID list == the release matrix | `PointerListsEveryMatrixRidTests` |
| publish jobs stay gated on manual dispatch from `main` | `ReleasePublishGateTests` |

Other repo-specific test shape:

- `Fixtures/<case>/` holds eight small **real** solutions (the dispatch shapes). They are indexed by
  `GoldenGraphTests` against checked-in snapshots but are `Compile Remove`d from the test assembly —
  their sources would otherwise collide with the test code. The harness never restores or builds
  them, so fixtures must declare their handler interfaces locally and reference no NuGet package.
- `GraphFixture.cs` builds a deliberately hostile mini-solution in a temp directory (same class name
  in two namespaces, three implementations of one interface with two registered, every registration
  form, a dependency loop) rather than committing `.cs` files into the repo.
- Assert golden snapshots by `Node.Key`, never by positional `Id`, so a diff reads as a real change.
- Three collections exist because xunit runs classes in a collection non-concurrently; a new test
  belongs in one if it touches the shared resource:
  - `[Collection("console-capture")]` — anything redirecting `Console.Out`/`Error` (commands and MCP
    capture stdout).
  - `[Collection("env-mutation")]` — anything mutating process environment variables
    (`CLAUDE_CONFIG_DIR`, `COPILOT_HOME`, `XDG_CONFIG_HOME`, …).
  - `[Collection("telemetry-state")]` — anything asserting on `Telemetry.Current` (every command
    resolves its budget into that singleton).

## Notes for working in this tree

- Only one csmesh process may write a given `.csmesh/`: two binaries sharing one index contend on the
  lock and answer exit `75`. Do not run a Windows binary and a WSL binary against the same tree at
  once.
- `Microsoft.CodeAnalysis.CSharp` and the xunit stack are the only package references; adding another
  is a decision worth raising rather than assuming.
- `.csmesh/`, `skills-lock.json`, and the generated agent/MCP config files are gitignored on purpose.
  `patches*/`, `deliverables/`, and `commits.txt` are local scratch that is **not** ignored — never
  blanket-stage them.
- `AGENTS.md` currently opens with a stale header copied from an unrelated project (`TheDnsSite`) and
  cites files that do not exist here (`DESIGN.md`, `settings.reference.yml`, `docs/configuration.md`).
  The csmesh-generated block at its end is correct; the prose above it is not.
