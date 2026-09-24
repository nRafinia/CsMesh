# ADR 0003: Package assemblies from obj/project.assets.json

- **Status:** proposed
- **Date:** 2026-09-24
- **Extends:** ADR 0002 (one compilation per in-scope project)
- **Supersedes:** the bin-only package collection in `Indexer.ReferenceSet`

## Context

`Indexer.ReferenceSet` (`src/CsMesh/Analysis/Indexer.cs:791`) builds the metadata-reference list
from two sources: the .NET shared framework and its installed siblings (`Indexer.cs:862-895`), and
every `bin/` assembly reached by a bounded walk (`Indexer.cs:897-914`). Package assemblies are
therefore taken **only** from `bin/`.

A class library does not copy its `PackageReference` closure to `bin/`; only an app or test project
does. Whether a package type binds therefore depends on whether some app project in the tree happened
to copy that package. Measured:

| solution | in-scope projects | assemblies loaded from `bin/` | unbound call sites |
|---|---|---|---|
| S2 (library-heavy) | 13 | 52 | 319 |
| S2, `bin/` emptied | 13 | 0 | 5665 |
| S1 (app-heavy) | 29 | 48 | 0 |
| CsMesh | 2 | (clean) | 0 |

The 0.6.1 release binary produces the same numbers as 0.7.0 on the same tree, and the
reference-collection code is identical between the tags (`git diff v0.6.1 v0.7.0 --
src/CsMesh/Analysis/Indexer.cs` adds only the review `referenceRoot` argument). The variable is
`bin/`, not the version, and it swings the unbound count by ~18x on one restore or build.

The compiler's own input for a project's dependencies is `obj/project.assets.json`, written by
restore. It is plain JSON and names, per target framework, the exact compile-time assembly for every
package. Item 1 measured it directly:

| solution | in-scope projects | with assets | TFMs | compile assets (excl. `_._`) | resolve under `packageFolders` | assembly names at >=2 versions across projects | frameworkReferences uncovered by the shared framework |
|---|---|---|---|---|---|---|---|
| S2 | 13 | 12 | net10.0 | 1132 | 1132 | 28 | 0 |
| S1 | 29 | 29 | net10.0 | 376 | 376 | 3 | 0 |
| CsMesh | 2 | 2 | net10.0 | 20 | 20 | 0 | 0 |

Every compile asset resolves to a file on disk under a listed `packageFolder`, and every compile
asset is under `lib/` (0 under `ref/`). One S2 project has no assets file (never restored).

## Decision

For each in-scope project, add that project's `obj/project.assets.json` compile assets to its
compilation's metadata references, keep the shared framework and the `bin/` walk as the fallback, and
give each project its own reference list. Bump the graph format v14 -> v15.

### 1. Source order and fallback

Per project, references are assembled in this order:

1. Shared framework and its installed siblings (`Indexer.cs:862-895`), unchanged.
2. That project's compile assets from its own `obj/project.assets.json`, resolved under its
   `packageFolders`.
3. `bin/` assemblies (`Indexer.cs:897-914`), unchanged, now a per-project fallback rather than the
   only package source.

The assets file decides the package types; `bin/` still supplies out-of-scope projects and anything
an app copied that the assets file does not cover. When the assets file is absent (never restored),
the project keeps today's shared-framework + `bin/` behaviour and doctor records it as not restored.
When the file is present but an individual compile asset is missing from disk, that one package falls
back to its `bin/` copy and the missing count is reported rather than failing the index. The fallback
is per project, not global: one unrestored project must not remove another's package references.

Bug prevented: a restored `obj/` with an unbuilt `bin/` compiling against no packages, and an
unrestored project silently dropping only some of its packages.

### 2. Per-project sets, not one global set

`CreateCompilations` (`src/CsMesh/Analysis/Indexer.Compilations.cs:126`) currently receives one
`List<MetadataReference>` and copies it verbatim into every project's compilation
(`Indexer.Compilations.cs:204`, `:211-212`). It must instead receive a per-project reference list
keyed by project directory, because the packages are per project.

A single global union of every project's compile assets would re-introduce the leak ADR 0002
removed: a package referenced by one project would become visible to every project's compilation,
and the version differences item 1 found would collapse into one arbitrary choice. Package visibility
is exactly a project boundary, and the split is what keeps project boundaries real.

Bug prevented: a package private to one project binding in another, and one project's resolved
package version silently replacing another's.

### 3. Compile section, not runtime; ref and lib

Read `targets.<tfm>.<package>.compile` and nothing else. Do not read `runtime`, which is the
execution graph and can differ from what the compiler sees (compile-only facades, a different asset
choice). Skip `_._` entries: they mark a package that contributes no compile asset, and adding them
as references is meaningless.

The `compile` section already names the correct asset whether it lives under `lib/` or `ref/`, so
reading `compile` prefers a package's `ref` assembly when it ships one, with no separate rule. In all
three measured solutions every compile asset is under `lib/` and none under `ref/`, so the
distinction is a no-op here but must not be hard-coded to `lib/`.

Bug prevented: referencing an assembly the compiler would not use, and turning a `_._` placeholder
into an empty or duplicate reference.

### 4. Version-conflict policy

Use each project's assets file exactly as resolved; do not merge versions across projects.
`project.assets.json` is NuGet's resolved graph, so within one project every package has exactly one
version. Across projects the same assembly simple name appears at several versions — 28 names in S2,
3 in S1, 0 in CsMesh, mostly framework-extension packages and a few third-party ones. Because
references are per project (decision 2) those versions never meet in one compilation; the only
cross-source rule is that a simple name already supplied by the project's compile assets or the
shared framework wins over a same-named `bin/` copy, which the existing `seen` set
(`Indexer.cs:828`) already enforces.

A global set would have to pick one version and would be wrong for every project that needed
another, and could put two versions of one assembly in one compilation (CS0433, or a silent
coin-toss binding).

Bug prevented: two versions of an assembly in one compilation, and a project binding another
project's resolved version.

### 5. Reading the file, AOT-safe

Parse with `System.Text.Json` through the source-generated context in
`src/CsMesh/Common/AppJsonContext.cs`, adding the assets DTO to it if a typed model is used; no
reflection-based serialization and no new package reference. `project.assets.json` is ordinary JSON.
The path is deterministic per project: `<project directory>/obj/project.assets.json`. The files
measured 251 KB (S2) and smaller; read once per project per index. Missing, truncated or unparsable
files fall through to decision 1 rather than throwing.

Bug prevented: a reflection-based deserializer that Native AOT cannot use, and a new dependency for a
file restore already writes.

### 6. Cache key and freshness inputs

Two existing caches ignore `project.assets.json` and must stop:

- **Review base cache.** `GraphStore.ReferenceKeyFor` (`src/CsMesh/Storage/GraphStore.cs:298`)
  hashes the indexer version, the graph format, the shared-framework path, and every `bin/` DLL by
  name, size and write time (`GraphStore.cs:300-315`); the cache path is `BaseGraphPathFor`
  (`GraphStore.cs:324`), called from `ReviewCommand.cs:273`. It must also fold in each in-scope
  project's `project.assets.json` (path plus size and write time, or a content hash). Without that,
  a base graph built before a restore is reused against a different package set and review reports a
  false exit 5.
- **Incremental freshness.** `GraphStore.DirtyFiles` (`GraphStore.cs:423`) compares only `.cs`
  `FileStamp`s plus the directory gate (`DirectoriesChanged`, `GraphStore.cs:460`). A changed
  `project.assets.json` is under `obj/`, is not a stamped file, and a zero-dirty tree short-circuits
  (`Indexer.Incremental.cs:81`); so today a restore is invisible. Add each in-scope
  `project.assets.json` as a tracked freshness input so a restore marks the index dirty.
  `ReferenceSet` is already recomputed on the incremental path (`Indexer.Incremental.cs:223`), so
  once it reads assets the rebind follows correctly.

Bug prevented: an index that keeps serving a pre-restore reference set while reporting freshness
clean, and a review base built against the wrong references.

### 7. The doctor references line

`DoctorCommand` prints `references N total -- R runtime, O from bin/`
(`src/CsMesh/Commands/DoctorCommand.cs:122-126`), and the empty-`bin/` branch says the solution was
not built (`DoctorCommand.cs:134-139`; the same warning in `IndexCommand.cs:204-207`). Once packages
can come from assets, that line must distinguish the three sources — for example
`R runtime, P from assets, O from bin/` — and the empty-`bin/` branch must not claim "not built" when
the assets files supplied the packages; the reverse message (packages missing because several
projects are not restored) becomes the actionable one.

Bug prevented: doctor telling the user to build when the fix is to restore, and telling them restore
is fine while `bin/` is genuinely the only source of a needed assembly.

### 8. Graph.CurrentFormatVersion

Move 14 -> 15. The persisted `Graph` gains a reference-source count so doctor can report offline
which source supplied the packages (`PackageReferences`, alongside the existing `ReferenceCount`,
`RuntimeReferences` and `OutputReferences` in `src/CsMesh/Models/Graph.cs:184-199`). AGENTS.md
requires a bump on any change to node keying, edge semantics, or on-disk shape; node keying and edge
semantics are untouched, but adding a persisted field is an on-disk shape change, so the rule
applies. The cost is the usual one: `Load`, `LoadPrevious` and `LoadBaseGraph` reject a mismatched
format, so the current graph rebuilds and the review base cache is re-indexed.

A design that reads assets but persists no new field would not move the version. We accept the bump
because the bug is invisible without provenance: the difference between "0 from `bin/` because
nothing was built" and "0 from `bin/` because assets supplied everything" is the whole diagnosis, and
doctor must state it from the stored graph.

Bug prevented: an on-disk graph whose reference provenance cannot be reconstructed offline, and
caches silently reused across a shape change.

### 9. Regression test shape

A temp-dir solution with one project: a hand-written `obj/project.assets.json` naming a local
`packageFolder`, into which the test emits a package assembly with Roslyn (`CSharpCompilation.Emit`)
before indexing; the source file calls a type in that assembly. The assembly is never copied to
`bin/`, and the fixture restores from no feed, so the "fixtures reference no NuGet package" rule
holds. Index the temp solution and assert the call binds — the graph has the call edge, and doctor
reports no `call/no-candidate-symbol` for it. Under today's bin-only collection the reference is
absent and the assertion is red.

A second assertion covers decision 6: after a successful index, rewriting only the temp project's
`project.assets.json` and re-running `csmesh index` must not report the tree current.

This is the smallest shape that is red on the bug: every existing fixture is package-free, so no
fixture ever required a package assembly the build had not copied, which is why the suite stayed
green.

Bug prevented: any change that drops or mis-resolves the assets source has a red test, and a refresh
that ignores a restore has one too.

### 10. Commit plan

Ordered, each green on its own:

- A. This ADR.
- B. `feat(index)`: parse each in-scope project's `project.assets.json` compile assets, per-project
  reference lists through `CreateCompilations`, shared-framework and `bin/` fallback. No format bump
  if provenance is not yet persisted.
- C. `feat(doctor)`: three-source references line, `PackageReferences` on the graph, format
  v14 -> v15, `ReferenceKeyFor` and `DirtyFiles` inputs.
- D. `test(index)`: the temp-dir package-assembly fixture and the restore-freshness assertion.

Each behaviour commit carries its own red-on-revert regression test, per AGENTS.md.

## Consequences

- Resolution no longer depends on whether an app project copied a package: a restored `obj/` is
  sufficient even with an empty `bin/`.
- Per-project reference lists are a further step away from the one global set, consistent with
  ADR 0002; `CreateCompilations`'s signature changes.
- Reference collection now reads files under `obj/`, so freshness and the review cache key must
  track them (decision 6).
- The graph format moves once for the provenance field; everything that rejects a format mismatch
  rebuilds.
- `doctor`'s "not built" advice becomes conditional on whether assets supplied the packages.
