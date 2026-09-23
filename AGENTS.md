# AGENTS.md — CsMesh

Standing rules for every session in this repository. A prompt may override a
rule by naming it; otherwise every rule below holds, and prompts do not repeat
them.

## Source of truth

- The code, then `docs/adr/`, then the prompt. When the code contradicts a
  prompt, the code wins: report the wrong premise with file and line and stop
  that item. Do not build around it.
- Data before code. A diagnosis-only item is answered and stops there; nothing
  is patched from a guessed cause.
- Answer every numbered item of the prompt, or write "not done" and why.
- Stop at the end of the prompt and wait for a go.

## Environment

- .NET 10 SDK. There is no `global.json`; do not add one.
- The checkout is CRLF on disk with `core.autocrlf=true` on Windows git, and
  there is no `.gitattributes`. Run git from Windows git only: WSL git sees
  every file as modified, and committing through it creates a line-ending
  churn commit across the tree. Keep each file's line endings as checked out.
- Never run tree-wide `sed`/`perl` through the PowerShell-to-WSL quoting
  layer. Edit files one at a time with the edit tool. If a mechanical
  multi-file change is unavoidable, write the script to a file, run it, and
  show `git diff --stat` before staging.
- Never stop `dotnet` processes you did not start.

## Git

- One logical change per commit, messages in the repo's style:
  `feat(index): …`, `fix(query): …`, `docs: …`, `chore(release): …`.
- `git status` before each commit. Stage by explicit path only. Untracked
  scratch (`patches*/`, `deliverables/`, `commits.txt`, `skills-lock.json`,
  `CLAUDE.md`) is never staged, ignored, moved or deleted.
- Deliver with `git format-patch <base>..HEAD -o deliverables/<series>/`, one
  directory per series.
- No push, no merge, no `workflow_dispatch`. Naser merges and releases.
- `CsMesh.csproj` `<Version>` and `npm/package.json` are touched only when the
  prompt asks.
- A commit on your branch that you did not make: report its hash and files
  under deviations. Do not drop, squash or rebase it away.
- `.csmesh/` is never committed.

## Code

- Native AOT is the shipping shape: JSON through the source-generated
  `Common/AppJsonContext.cs`, no reflection-based serialization, no runtime
  code generation. The only package references are
  `Microsoft.CodeAnalysis.CSharp` and the xunit stack; no new one without
  asking.
- MSBuild is never evaluated. Solutions and projects are read as XML; that
  constraint is the tool's startup time and AOT compatibility.
- MCP tools forward to the CLI command implementations. No query logic in
  `Mcp/`; the two paths must not be able to drift.
- All answer text goes through `BudgetWriter` / `Emit`. Nothing else reaches
  stdout in JSON or MCP mode.
- Exit codes are the public contract: 0 ok, 1 not found, 2 over budget,
  3 ambiguous, 4 no index / format mismatch / baseline behind, 5 unaccepted
  structural change (`review`), 64 usage, 70 internal, 75 index contended.
  No new code and no changed meaning unless the prompt asks.
- Bump `Graph.CurrentFormatVersion` on any change to node keying, edge
  semantics or on-disk shape.
- Node identity is `Node.Key`. Never key, compare or assert by `Id` or by
  position.
- XML docs explain why and name the bug the design prevents. No `// TODO`,
  no placeholders, no scaffolds.

## Evidence and tests

- Every behaviour commit carries its own regression test in the same commit,
  shown red on revert: revert the behaviour, run the test, paste the failure,
  restore. A named but unrun mutation is not evidence. Docs-only and pure
  refactor commits need no red run; say so.
- No skipping or placeholder test, except one that needs an external tool and
  skips with a clear reason when it is absent.
- A test touching `Console.Out`/`Error`, process environment variables or
  `Telemetry.Current` joins the matching collection: `console-capture`,
  `env-mutation`, `telemetry-state`.
- `Fixtures/<case>/` solutions reference no NuGet package and declare their
  handler interfaces locally. Golden snapshots assert by `Node.Key`.
- The suite runs on Windows: file-sharing violations and lock-file access are
  real failure modes there. Design tests for them.
- Build once, then `--no-build`. Use `--filter` while working.
- End of prompt, once: full `dotnet test` with a build after the last commit;
  the reported count comes from that run. Publish Native AOT only when the
  prompt asks, and then report the warning count and the `IL2xxx`/`IL3xxx`
  count.
- A change to indexing is verified with `csmesh index --full` on a real
  solution before it is claimed to work.

## Private test solutions

- Measurements use private solutions. Their names, paths and access limits
  come from `AGENTS.private.md` (git-excluded; read it if present) or from the
  prompt.
- Their names, paths and symbol names never appear in anything tracked and
  are never printed in a report: placeholders and counts only.
- Before the last commit, sweep every tracked file with a pattern built from
  the private solutions' indexes: in-source short names that are compound
  PascalCase (at least two capitalized words, `^I?[A-Z][a-z0-9]+[A-Z]`), minus
  every name, or dotted component of a name, that this repository's own index
  declares, plus the literal list from `AGENTS.private.md`. Single-word names
  identify nothing and are left out. Hits must be zero: any hit is a leak.
  Report the pattern size and the hit count, never the pattern.
- `--no-telemetry` on every csmesh invocation.

## Token economy

The csmesh block at the end of this file is generated by `csmesh skill` and
belongs to Naser. Never edit between its csmesh-instructions markers and never
run `csmesh skill` yourself. Its commands, budgets and exit codes apply as
written; the rules below add to it and win where the two disagree.

- Navigate with the installed `csmesh` on PATH before opening files, then read
  only the line ranges it points to. If it rejects this repo's index with a
  format mismatch, report `csmesh --version` under deviations and navigate
  with the build under development instead, by full path.
- The build under development is otherwise for verification. When a
  verification needs this repository itself and a different binary owns
  `.csmesh/`, run it on a copy under `%TEMP%`.
- One csmesh platform per tree: never a Windows and a WSL binary against the
  same `.csmesh/`. Two binaries contend for its lock and answer exit 75.
- Keep the index current: `csmesh index` after edits, `csmesh index --full`
  after any change to a `.csproj`, `CsMesh.slnx` or a package reference. A
  reference-set change dirties no source file, so an incremental refresh
  misses it.
- If csmesh gives an answer the code contradicts, report the query, its output
  and the file and line under deviations.
- **rtk** filters exploration only: `git status`/`log`, `ls`/`tree`, grep,
  logs, and reading a file to understand it. Prefix it explicitly. csmesh
  output is already budgeted and is not piped through rtk. Before editing a
  file, read the exact line range raw.
- **Evidence is never filtered by rtk**: the full suite, a publish, every
  red-on-revert run, any diff the private-name grep reads, `git format-patch`,
  and any output captured for analysis. Run them raw with stdout and stderr
  redirected to a log file under `%TEMP%` (or `/tmp` in WSL), take the numbers
  from that file, and name the file in the report. Never `rtk summary` for
  evidence.
- Quiet `dotnet` instead of filtering it: `-nologo -v q -tl:off`, and test
  runs add `--logger "console;verbosity=minimal"`. `-clp:ErrorsOnly` is for
  working builds only; a publish keeps its warnings.
- Large files are read by section: grep for the heading or member, then read a
  line range. This applies to `README.md`, `npm/README.md`, `docs/index.html`
  and `Analysis/Indexer.cs`.

## Reports

Numbered to match the prompt's items. Then: commit hashes; red-on-revert per
commit; the full-suite count with its log file; wrong premises; deviations;
final `git status`. Plain lines, tables only for numbers. Paste only the lines
that carry the evidence. End with the one-line `rtk gain` summary.

<!-- csmesh-instructions -->
# csmesh: C# structural code intelligence

## MANDATORY DIRECTIVE: ALWAYS PREFER CSMESH OVER GREP / FILE SEARCH
In ANY repository with `.cs`, `.csproj`, `.sln` or `.slnx` files, you are STRICTLY FORBIDDEN from using
`grep`, `ripgrep`, glob, or reading files in sequence to discover C# symbols, trace callers, find
interface implementations, or inspect types.

Your VERY FIRST action when exploring or investigating C# code MUST be running `csmesh` (via your available shell/bash/command tool, or via `csmesh_*` MCP tools if configured):
- To find a symbol or route: `csmesh where <term>`
- To trace call paths: `csmesh trace <Type.Member> --budget 700`
- To find implementations: `csmesh impl <IThing> --budget 600`
- To check blast radius: `csmesh blast-radius <Type.Member> --budget 800`
- To inspect a type: `csmesh context <TypeName> --budget 900`
- To orient in a repo: `csmesh map`

Grep is ONLY permitted for exact string literals inside quotes, error messages, and non-C# files.

In any repository with `.cs`, `.csproj`, `.sln` or `.slnx` files, reach for `csmesh` before grep,
ripgrep, glob, reading files in sequence, or handing discovery to a subagent. It answers from a
prebuilt symbol graph in one shell call and resolves what text search cannot see: DI registrations
including assembly scanning, MediatR Send/Publish, interface dispatch, attribute routing.

## Match the thing you are about to do

| you are about to | run instead |
|---|---|
| spawn a subagent to "find everything about X" or explore how X works | `csmesh context X --budget 900` |
| list directories to orient in an unfamiliar repo | `csmesh map` |
| open a second file to follow a call chain | `csmesh trace Type.Member --budget 700` |
| enumerate call sites ("who calls this? where is it invoked from?") | `csmesh blast-radius Type.Member --depth 1` |
| guess which class implements an interface | `csmesh impl IThing --budget 600` |
| change a `public` member | `csmesh blast-radius Type.Member --budget 800` |
| grep for a mediator handler | `csmesh trace` on the calling method |
| search for an HTTP route or a background job | `csmesh entrypoints <filter>` |
| work out how A reaches B | `csmesh path <from> <to>` |
| claim an edit is safe | `csmesh diff --budget 800` |
| finish a refactor | `csmesh changes` |
| open a pull request, or gate CI on structural change | `csmesh review` (`--accept` once reviewed) |
| open a file to see a type's fields and nullability | `csmesh context TypeName`, read `MEMBERS` |

A subagent is for what csmesh cannot know: intent, naming, business rules, why a decision was made.
Not for where things are and what connects to what.

## When you have words, not a symbol name

Every other command takes a symbol. `csmesh where <term>` is the one that finds it. Do not grep
first. It searches names, namespaces, file paths and route templates, ranks by how many entrypoints
reach each hit, and prints the next command already filled in.

```
csmesh where discount        # -> CheckoutService.ApplyDiscount, then trace it
csmesh where "POST /orders"
```

## When something comes back empty

- **Exit 2**: the answer was too large, not absent. In order: the depth the message names, then
  `--under <path>`, then `--depth 1` for direct edges only. `silence` is for a *narrowed* query that
  then exits 1 -- never for exit 2 itself, where nothing was missing.
- **Exit 1**: do not fall back to grep. Run `csmesh silence <symbol>` (or `<from> <to>`). It says
  whether the symbol was mistyped, lives in a package, was never bound because the solution was not
  built, or is reached only through a container scan. Only one of those is fixed by searching here.
- **Exit 3**: the name matches symbols in more than one project. The candidate list prints each
  one's project; re-run with `--project <path>` taken from it, or with a qualified `Type.Member`.
- **Thinner than expected**: `csmesh unresolved` reports where the indexer failed and why.
- **`[STALE]` rows**: re-run with `--heal`. The changed files are rebound before the answer.

Keep using grep for string literals, config values, TODOs, error messages, and non-`.cs` files.

## Reading the output

- `[impl, di-bound]` -- registered in the container; this is the one that runs.
- `@ Api/Registrations.cs:22` -- where the edge was wired up. Go there; do not search for it.
- `{test}` -- test code: a real caller, but not what breaks in production.
- `?0.70 short-name-match` / `?0.75 assembly-scan` -- inferred, not read off a compiler symbol.
  **Below 0.80 is a lead, not a fact.** No `?score` means exact.

## Exit codes

`0` ok, `1` nothing found, `2` over budget, `3` ambiguous, `4` no index (or, for `review`, an
index behind HEAD), `5` `review` only: unaccepted structural change, `64` bad command line
(including `review --accept` behind HEAD), `70` internal error, `75` index write contended.
Branch on these; do not parse the text.

## Practice

- **Nested types**: keys use the immediate containing type, not the outermost class. `Builder`
  nested inside `Indexer` means the key is `Builder.LineRange`, **not** `Indexer.LineRange`.
  Use `csmesh where <member-name>` first to discover the correct qualified name.
- Always pass `--budget`: 700 `trace`, 600 `impl`, 800 `blast-radius`/`diff`, 900 `context`, 500 `path`.
- Narrow with `--under src/Api` before raising `--budget`.
- Prefer `Type.Member` over a bare name; a bare name costs a round trip via exit 3. When the name
  repeats across projects, exit 3 lists each candidate with its project: use `--project <path>`.
- On overflow, `trace` names a depth that fits and prints the command to re-run. Use it.
- csmesh tells you which files matter. Open those files. It replaces hunting for code, not reading
  the code you are about to change.
<!-- /csmesh-instructions -->
