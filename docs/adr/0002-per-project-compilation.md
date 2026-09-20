# ADR 0002: One compilation per in-scope project

- **Status:** accepted for implementation on `feature/per-project-compilation`
- **Date:** 2026-09-20
- **Supersedes:** the single whole-solution compilation in `Indexer.Build`

## Context

The indexer parses every in-scope `.cs` file into one `CSharpCompilation` named `csmesh.index`. Scope
is decided per file (nearest `.csproj` owns it), but every in-scope file lands in the same
compilation, so project boundaries exist only as a `Node.Project` string and do not isolate names,
global usings or assembly identity.

On a 13-project private solution (about 1,175 source files), that conflation accounted for roughly
51 of 59 unresolved call sites: 20 `CS0104` ambiguities, 14 `CS0314`, 8 `CS0311`, 7 `CS0452`,
8 `CS0246` and 1 `CS9338`, clustered by file. Splitting the compilation removes all of them. The
measured cost of compiling one project at a time is +25–30% wall time at the same peak working set.

## Decision

Build **one `CSharpCompilation` per in-scope project**, in topological order, each with a
`CompilationReference` to every project in its transitive `ProjectReference` closure, sharing one
metadata-reference set, and key symbols by declaring assembly. Bump the graph format from v12 to v13.

Two prerequisites land first, because a per-project compilation without them would move files
around while leaving the wrong global usings and stale frameworks in place:

1. deterministic target-framework selection for `obj/` and `bin/` (landed), and
2. implicit-usings synthesis for a project whose `obj/` is missing (landed).

## 1. Compilation unit and references

- One compilation per in-scope project. In-scope projects are those `ProjectScope` leaves in, from a
  solution file or the `ProjectReference` closure from executables and test projects.
- Each compilation references every project in its **transitive** `ProjectReference` closure as a
  `CompilationReference`. A `CompilationReference` exposes only the referenced compilation's own
  declarations; its `References` are not visible through it, so the closure must be added
  explicitly. A cycle is broken with a log line rather than recursed.
- Compilations are created in topological order and **one instance per project is reused** as the
  reference for all dependents. Rebuilding the same project separately for each consumer would give
  one type different symbol identities in different compilations.
- `Builder` currently assumes one compilation (`comp.GetSemanticModel(tree)`, `comp.SyntaxTrees`,
  `comp.AssemblyName`). It becomes a per-tree → owning-compilation map.
- Out-of-scope projects remain metadata references from `bin/`; dropping them would trade a false
  positive for a silent gap.
- **One shared factory.** `Build` (full) and `BuildIncremental` construct the compilation in two
  places today. Both must be routed through a single `CreateCompilations` function and switch to the
  per-project model in the same commit. A half-switched state — full build per-project, incremental
  whole-solution, or the reverse — produces two graphs with different identity rules for the same
  source.

## 2. Membership and files a real build compiles

The nearest `.csproj` owns a file, already implemented. A loose file with no `.csproj` ancestor is
kept today; the proposal is to exclude it when the repository has any `.csproj` and keep it only
when it has none, and to report the excluded count in `doctor`.

CsMesh reads raw XML and never evaluates MSBuild, so it does not understand the ways a build pulls
in source from outside a project directory:

- `<Compile Include="..\Shared\File.cs">` (with or without `Link`): the file is indexed iff its own
  nearest project is in scope, not because a csproj includes it. A linked file outside every project
  directory is treated as loose.
- `.shproj` / `.projitems` shared projects: not recognised as projects at all; their files are loose.
- `Directory.Build.props` / `Directory.Build.targets` that add `Compile` items: never read.

A read-only check of the 13-project private solution found **no** linked `Compile Include`, no
`.shproj`/`.projitems`, and a single `Directory.Build.props` containing no `Compile` items; it does
contain one `<Compile Remove>`, which CsMesh also ignores because it indexes the filesystem rather
than the compile item list.

**Loose-file exclusion is therefore not implemented in this branch.** CsMesh does not handle the
constructs, so excluding a loose file could silently drop source a real build compiles. The split
should own them by parsing `<Compile Include>` (including `Link`) and `.projitems`, or at minimum by
not excluding a loose file that any csproj references. Until then the safe default — keep loose
files — stays.

## 3. Global usings, target framework and implicit-usings synthesis

**Landed before the split.**

- A source-level `GlobalUsings.cs` is an ordinary `.cs` file and becomes per-project automatically
  when the file set is partitioned by owning project.
- SDK-generated sets are read per project from **one target framework**: `TargetFramework`, else the
  first of `TargetFrameworks`, with the newest framework directory on disk only as the fallback for a
  framework that was never built. Before this, every `*.GlobalUsings.g.cs` under `obj/` was compiled
  into the solution, so a project retargeted from net8 to net10 contributed both sets; on the private
  solution that was 9 distinct sets, including net8 sets applied to net10 files. There is in fact no
  multi-targeting there — the extra trees are stale build history.
- `bin/` is read through the same chosen framework, so a stale framework's copy of a package cannot
  shadow the current one.
- A project with no `obj/` but with `<ImplicitUsings>enable</ImplicitUsings>` gets the set the SDK
  would have written, reconstructed from the csproj: the base namespaces always, the ASP.NET Core
  namespaces only for the Web SDK. A project that does not opt in gets nothing. This closes the
  ~35 `CS0246` that surface in a no-`obj/` test project once a neighbour's global usings stop
  covering for it.
- A repository with no csproj keeps the historic one-size-fits-all set, because there is no project
  to read a framework or an opt-in from.

## 4. Metadata references

Build `MetadataReference` instances once and share them across compilations. Each in-scope project is
represented by source (`CompilationReference`), never by its `bin/` assembly.

The global "shadow every live output" sweep does **not** go away. If each compilation excluded only
its own output and its closure's outputs, an unrelated in-scope project's DLL would remain in the
shared set and leak its types into every compilation. The correct rule is to keep every in-scope
project's output out of the shared metadata set globally, and add `CompilationReference`s for the
closure. Per-compilation exclusion of the closure's DLLs is then redundant. The sweep's meaning
changes from "avoid duplicate types" to "keep every in-scope project sourced from source".

## 5. Symbol identity across compilations

Diagnosis before changing keys. A scan of `Analysis/` found **no `Dictionary` or `HashSet` keyed by
an `ISymbol`, no `SymbolEqualityComparer` use, and no `ReferenceEquals` on symbols**. Identity is
always reduced to:

- `Key(symbol)`: a string from `ToDisplayString(FullyQualifiedFormat)`, used by `_idByKey`;
- `RequestKey(symbol)`: a string, used by the dispatch tables;
- node ids (integers), used by `_implementorsByBase`, `_diBoundPairs`, `_dedupe`.

A scratch probe (not committed) built compilation A, B referencing A through a `CompilationReference`
with the same framework, and C referencing A with a different corlib to force retargeting. For a
type, a method and a generic method:

| Case | `ContainingAssembly.Name` | `Key` output | `SymbolEqualityComparer.Default.Equals` vs A | `ReferenceEquals` |
|---|---|---|---|---|
| B, same framework | `A` | identical | true | true |
| C, retargeted | `A` | identical | **false** | **false** |

Two compilations that share one `MetadataReference` yield the **same** `PEAssemblySymbol` instance
(`ReferenceEquals` true).

Consequences:

- The string-key design survives both cases: `Key` and `ToDisplayString` are identical even when the
  symbol instance and the default comparer disagree. Any future map keyed by `ISymbol` with the
  default comparer would **not** survive retargeting, so the string key is the declared identity and
  must stay so.
- `ContainingAssembly.Name` is stable across retargeting, so prefixing keys with it is safe. Use the
  compilation's assembly name (MSBuild assembly name: `<AssemblyName>`, else the project file name;
  not `<PackageId>`).
- Prefix both `Key` and `RequestKey`; otherwise two projects that declare the same fully-qualified
  name still merge into one node and one dispatch entry.
- Duplicate assembly names are fatal to this scheme. A repository indexed with `--all` can contain
  several projects that share a file name (the test tree has eight nested fixture csprojs, all named
  `Fixture`). The split must key projects by full path and either disambiguate compilation names or
  refuse the collision.

## 6. Tables once models come from several compilations

| Table | Needs |
|---|---|
| `_idByKey` | Assembly-qualified keys; shared across all compilations (one `Builder`), not per compilation. |
| `_implementorsByBase` | Resolve base symbols across `CompilationReference`s; node ids stay global. |
| `_handlersByRequest` | Assembly-qualified `RequestKey` so a request and its handler in different projects match; persisted, so v13 makes v12 tables unreadable (correct). |
| `_requestKeysByShort` | Keys become assembly-qualified; short-name fallback unchanged. |
| `_diBoundPairs` | Node-id pairs, already global; not persisted. |
| `_dedupe` | Global set; still must be seeded on incremental. |
| dispatch export/seed | Structurally unchanged; only key content changes. |
| **`methodsByOwner` (pass 1)**, **`methodByShort` / `methodsByOwner` (pass 3)** | The list above omits these. They are keyed by short display name, so two same-named types in different assemblies merge and interface/override member linking can target the wrong method. They must key by assembly-qualified owner. |
| `ExternalTypes` | Keyed by simple name only; same-named external types merge across assemblies. |

`Graph.Resolve` short-name matching is unchanged, but now genuinely returns multiple hits where
duplication is real, raising exit 3 more often.

## 7. Incremental

For v1, rebuild all compilations and bind only the dirty files (`OnlyFiles`), which is cost parity
with today: parsing is the cost and binding was already restricted.

The later optimisation — rebuild only edited projects plus their transitive dependencies — must not
reuse a cached dependent compilation that embeds a `CompilationReference` to a rebuilt dependency.
That stale reference resolves an edited file against obsolete symbols, producing a wrong edge with
no error. Define the rebuild set as every project containing a dirty file plus its transitive
dependencies, and invalidate any cached compilation whose referenced project was rebuilt. Rebuilding
*consumers* of an edited project is not required for edge correctness, because edges are only
re-derived from dirty files; it is required only if such a consumer is itself dirty. Assembly-name
or hash must be part of the cache key so a rename invalidates.

## 8. Diagnostics

Collect declaration diagnostics **per compilation**, attach the project name, and aggregate in
`doctor` under a per-project grouping. A collision that clusters in one project should read as that
project's problem, and the current flat top-8 list hides it.

## 9. Format, baseline and compatibility

- `Graph.CurrentFormatVersion` 12 → 13. `Load`, `LoadPrevious` and `LoadBaseGraph` already reject a
  mismatch, so the current graph must be rebuilt and a cached base graph is re-indexed.
- `BuiltByVersion` already forces `--full`; the `--full` is required because scope, global usings and
  keys all change.
- `changes` against a v12 previous graph returns exit 4 ("no previous index"). `review` against a v12
  current graph returns exit 4 ("no usable index"); its v12 base cache is ignored and rebuilt.
- **Baseline across the format change.** `.csmesh/accepted.txt` stores finding ids derived by hashing
  the edge signature, which includes both `Node.Key`s. Prefixing keys with the assembly changes every
  hash, so after the bump every previously accepted finding would re-surface as unaccepted — a gate
  that suddenly reports the whole history. The proposal is to record the graph format in the
  baseline and, on a mismatch, exit **4** with "baseline predates format vN → csmesh review --accept",
  never 5.
  - Exit 4 fits the contract: it already means "the index exists but cannot answer this question"
    (the commit-gap case uses it). A stale baseline is the same shape — the comparison cannot be
    trusted — whereas exit 5 means "findings were produced", which is exactly what a gate reacts to.
    Exit 64 is for a bad command line and does not apply.
  - Unlike the commit-gap case, `--accept` under a baseline mismatch is the remedy, not a usage
    error, so it must be allowed and must write the new ids.
  - The drift guard (`DocumentedContractTests`) compares the *set* of emitted exit codes to the
    documented tables, so reusing 4 needs no table change. The review help text should name the
    remedy.
- Apart from `accepted.txt`, nothing persists a key-derived hash. The graph files store raw `Key`
  values and are already format-gated; the usage log stores query text, not keys.

## 10. Tests

Golden fixtures needed: a cross-project global-using collision; two top-level `Program`s; a
cross-project call edge; an implementor, a MediatR handler and a DI registration each declared in a
different project from its abstraction; and a multi-targeted project. Existing coverage is partial:
the shadowing fixture already exercises a cross-project call in one compilation, and the implicit
test a single set, but the golden dispatch fixtures are single-project and must not contain `obj/`.
New tests are added with the split; the TFM and synthesis behavior already has its own
(landed) fixtures.

## 11. Commit plan

Ordered, each green on its own with a regression test. Only the keying commit bumps the format.

A. `test(review)`: end-to-end review hygiene (git status clean, `.gitignore` untouched,
   `info/exclude` has `.csmesh/`). Landed.
B. `feat(index)`: per-project deterministic target framework for `obj/` and `bin/`. Landed.
C. `feat(index)`: synthesize implicit usings when `obj/` is missing. Landed.
D. Split: one compilation per project, `CompilationReference` closure, topological order, shared
   metadata references, per-compilation diagnostics, and **one shared
   `CreateCompilations`** used by both full and incremental builds. **No key change.** Every golden
   fixture must stay **byte-identical**: a single-project fixture produces exactly the same
   compilation as before, and `Render` is untouched. No format bump.
E. Assembly-qualified keys (`Key` and `RequestKey`), pass-1/pass-3 owner keying, duplicate
   assembly-name handling. **Bumps v12 → v13.** Regenerates golden snapshots and adds the
   two-`Program` and duplicate-name exit-3 tests.
F. Baseline format stamp and the exit-4 guard (section 9). Depends on E.
G. *(later)* per-project incremental rebuild with dependency-aware invalidation (section 7) and the
   benchmark harness.

## 12. Golden snapshot format versus graph format

The `# format 2` line at the top of each `expected.graph.txt` is the **snapshot text schema
version**, hard-coded by the golden renderer. It tracks the layout of the snapshot sections
(nodes/edges/unresolved column order, the header, how an ambiguous name is displayed). It is not
`Graph.CurrentFormatVersion`: the graph format describes the on-disk graph, while the snapshot
format describes a checked-in text file. Bump the snapshot version when the renderer's text changes;
bump the graph format when the persisted graph changes. Commit D must change neither. Commit E bumps
the graph format to v13 and, because the keys in the nodes section change, regenerates the snapshots
without bumping the snapshot format.

## Consequences

- Collisions that today become one merged node become two nodes, so short-name queries can now
  return exit 3 where they previously returned a silently wrong single answer.
- Every in-scope project must have a unique assembly name, or the split must disambiguate.
- A stale baseline is refused rather than reported, so a gate does not fire on its own upgrade.
- Global usings are no longer a cross-project leak, which is a behavior change users will notice as
  previously binding names becoming unbound — and the synthesis and TFM work make that change agree
  with the build.
