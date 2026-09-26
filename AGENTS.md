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
- Uncommitted changes you did not make: report the file and `git diff --stat`, then stop.
  Never restore, stash or discard them

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
- Before the last commit, run `eng/private-name-sweep.ps1 -Base <series base>`.
  It builds the pattern from the private solutions' indexes (roots and
  literals from `AGENTS.private.md` or its parameters), sweeps every tracked
  file and the series' commit messages and author fields, and exits zero only
  when nothing hit. Report the pattern size and the hit count, never a term,
  assembly or project name.
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
