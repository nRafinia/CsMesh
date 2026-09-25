# ADR 0004: `csmesh export` — Mermaid and DOT

- **Status:** accepted
- **Date:** 2026-09-25
- **Extends:** ADR 0002 (one compilation per in-scope project); ADR 0003 (package assemblies from `obj/project.assets.json`)

## Context

Every csmesh answer is text. `map` says which projects lean on which, `entrypoints` lists the ways in, `trace` and `blast-radius` walk the edges — but nothing emits a diagram a person or a design doc can read. `export` renders the graph as **Mermaid** or **DOT** at three levels: the **project** graph, the **namespace** graph, and a **symbol neighbourhood** around one member. This ADR fixes the shape of that command; it does not implement it.

Two questions decide most of the design, and both were measured before writing this: what a rendered graph costs against the token budget, and what can serve as a stable id.

### Measured sizes

A throwaway PowerShell script over each solution's `.csmesh/graph.json` (dev build, solution scope) collapsed the graph to each level, rendered Mermaid (`flowchart LR`) and DOT (`digraph`), and measured the text with `BudgetWriter.Estimate` (`src/CsMesh/Common/BudgetWriter.cs:81`: `ceil((len+1)/4)` per line). "Collapsed edges" are distinct `(from, to, kind)` triples after mapping both endpoints to the level's buckets. An edge whose both ends map to one bucket is **internal**: it is not drawn (a self-loop carries no structure) and is counted in the summary instead, so it is not in the edge total below.

| level | solution | nodes | collapsed edges | Mermaid chars | Mermaid tokens | DOT chars | DOT tokens |
|---|---|---|---|---|---|---|---|
| project | CsMesh | 3 | 4 | 221 | 59 | 291 | 76 |
| project | S | 21 | 105 | 4352 | 1158 | 5609 | 1465 |
| namespace | CsMesh | 11 | 50 | 1876 | 485 | 2435 | 616 |
| namespace | S | 49 | 241 | 9769 | 2501 | 12328 | 3111 |

**Bucket rule.** A project bucket is the node's owning project; a node with none -- a synthesized top-level program, a synthetic tuple/anonymous signature -- is the `(no project)` bucket. A namespace bucket is the namespace of the node's outermost containing type, never a type name: a nested type and its members land in the outer namespace, and a synthetic node with no determinable declaring type is the `(global)` bucket. A namespace no symbol declares is not a bucket. The counts above are corrected on 2026-09-25: the first measurement bucketed members under their declaring type name, so nested-type containers appeared as namespaces, and it drew same-bucket self-loops instead of withholding them as internal.

CsMesh's third project bucket is the empty project of a synthesized top-level node (see "Node identity"); the real project count is two. "S" is a second, private solution measured the same way.

Collapsed edges per kind:

| level | solution | Call | Construct | Interface | Override | DiBinding | Mediatr | Route | TypeUse |
|---|---|---|---|---|---|---|---|---|---|
| project | CsMesh | 3 | 1 | 0 | 0 | 0 | 0 | 0 | 0 |
| namespace | CsMesh | 38 | 12 | 0 | 0 | 0 | 0 | 0 | 0 |
| project | S | 51 | 33 | 15 | 1 | 5 | 0 | 0 | 0 |
| namespace | S | 151 | 66 | 15 | 1 | 5 | 0 | 1 | 2 |

The graph totals behind those collapses: CsMesh 2766 nodes / 8566 edges, of which 2397 are `TypeUse`; S 1945 / 5328, of which 1453 are `TypeUse`. Test-tagged nodes (`Node.Tags` contains `test`, `src/CsMesh/Analysis/Queries.cs:481`): 1047 CsMesh, 461 S; edges touching one: 3901 and 2526. Edges below the 0.80 trust threshold (`src/CsMesh/Models/Edge.cs:56`): **0** in both — S has exactly two edges carrying confidence, both 0.9. So the low-confidence rule below is defined but unexercised by either solution.

Symbol neighbourhoods: the three highest-degree symbols in each graph (degree = incident edges, both directions), breadth-first, undirected, induced subgraph at each depth.

| solution | symbol | degree | depth | nodes | edges | Mermaid chars | Mermaid tokens | DOT chars | DOT tokens |
|---|---|---|---|---|---|---|---|---|---|
| CsMesh | A | 263 | 1 | 263 | 420 | 20674 | 5412 | 26564 | 6857 |
| CsMesh | A | 263 | 2 | 454 | 1058 | 41133 | 10913 | 54293 | 14017 |
| CsMesh | A | 263 | 3 | 903 | 3454 | 117942 | 31693 | 156258 | 40704 |
| CsMesh | B | 196 | 1 | 197 | 325 | 17182 | 4497 | 21689 | 5649 |
| CsMesh | B | 196 | 2 | 784 | 2677 | 95146 | 25443 | 125517 | 32523 |
| CsMesh | B | 196 | 3 | 1733 | 6214 | 224068 | 59008 | 293864 | 75913 |
| CsMesh | C | 165 | 1 | 165 | 286 | 13562 | 3564 | 17462 | 4519 |
| CsMesh | C | 165 | 2 | 223 | 448 | 19039 | 4978 | 24861 | 6505 |
| CsMesh | C | 165 | 3 | 245 | 529 | 21597 | 5665 | 28324 | 7415 |
| S | A | 71 | 1 | 72 | 159 | 7706 | 1990 | 9719 | 2496 |
| S | A | 71 | 2 | 429 | 1819 | 58000 | 15670 | 77809 | 20226 |
| S | A | 71 | 3 | 867 | 3024 | 106897 | 28731 | 141055 | 36534 |
| S | B | 57 | 1 | 58 | 109 | 5363 | 1393 | 6814 | 1741 |
| S | B | 57 | 2 | 422 | 1910 | 60707 | 16347 | 81279 | 21153 |
| S | B | 57 | 3 | 899 | 3093 | 109312 | 29394 | 144347 | 37399 |
| S | C | 49 | 1 | 50 | 58 | 4261 | 1099 | 5189 | 1326 |
| S | C | 49 | 2 | 227 | 930 | 29962 | 8105 | 40154 | 10523 |
| S | C | 49 | 3 | 686 | 2523 | 87524 | 23537 | 115725 | 30021 |

Two facts fall out: the project level is small (≤1.6k tokens), the namespace level is already an order of magnitude larger, and **even a depth-1 neighbourhood of a hub runs to 5.4k tokens** — 263 neighbours and 420 edges is not a picture, it is the whole solution. Any default budget below a few thousand tokens will overflow on a real graph.

### Node identity — what can be an id

`Node.Key` (`src/CsMesh/Models/Node.cs:10-26`, built by `Builder.Key`, `src/CsMesh/Analysis/Indexer.cs:1570-1602`) is the compiler-derived identity: `assembly|container|self|kind`, with `self` = `Name`arity(params)` for a method. It is stable: two `--full` indexes of the unchanged CsMesh tree produced an identical multiset of 2766 keys (SHA-256 of the sorted keys matched). `Node.Id` (`Node.cs:8`) is not an identity — it is positional, assigned from the node count at insertion, so one added declaration renumbers everything after it (`Node.cs:11-20`).

The keys themselves are **not usable as bare diagram ids**. The distinct non-identifier characters actually present in CsMesh's keys are:

```
space   (   )   ,   -   .   :   <   >   ?   [   \   ]   `   |
```

`|`, `:`, `` ` ``, `.`, `(`, `)`, `,`, `<`, `>`, `[`, `]`, `?` and space are the key's own syntax. `-` and `\` come from one place: a synthesized top-level program is keyed `csmesh|toplevel::<absolute file path>|...`, so its key embeds the machine's path (`...\Temp\...\Program.cs`). Mermaid node ids are `[A-Za-z_][A-Za-z0-9_-]*`; DOT unquoted ids are `[A-Za-z_\200-\377][A-Za-z0-9_\200-\377]*`, and `.` and `:` are port separators, `|` is record-label syntax. Nothing in the key character set is safe unquoted, and the path embedding means a key-derived id also varies with checkout location.

### Traversal

Default depths are on `QueryCommand` (`src/CsMesh/Commands/QueryCommand.cs:45-53`): `blast` = 3, `trace` = 6, `context` = 3, `path` = 12. `Trace` (`src/CsMesh/Analysis/Queries.cs:92-169`) walks **forward** over `g.Out` filtered to `IsFlow` edges, dedups by node id keeping the shallowest level, guards cycles with an `onPath` set, and charges every emitted line to a per-level cost; on overflow it computes `DepthThatFits` (`Queries.cs:175`) and exits 2 naming a depth. `BlastRadius` (`Queries.cs:272-345`) walks **backward** over `g.In` excluding `TypeUse`, accumulates the minimum confidence along the path, and caps its display (`Take(30)` entrypoints, `Take(25)` indirect types, `Take(6)` projects).

Neither walker is the export neighbourhood: `trace` follows only flow edges forward, `blast-radius` only callers backward, and export needs an undirected `depth`-ring around a start. They share the primitives, not the walk: `g.Out`/`g.In` (`src/CsMesh/Models/Graph.cs:354,360`), the visited set keyed by `Node.Id` within one render, and the `BudgetWriter`/`DepthThatFits` overflow handling.

## Decision

### 1. Levels, CLI surface and defaults

`csmesh export [<symbol>] [OPTIONS]`, with `--format mermaid|dot` (default `mermaid`), `--level project|namespace|neighbourhood`, and `--direction in|out|both` (default `both`):

- no positional and no `--level` → **project**;
- `--level namespace` → **namespace**;
- a `<symbol>` positional → **neighbourhood**, `--depth` default **1**.

The symbol is resolved exactly like every other symbol argument, including the `Type.Member(int,string)` selector when the name is overloaded (`src/CsMesh/Analysis/SymbolSelector.cs:295`). `--depth` and `--direction` apply only to the neighbourhood level; `--depth 0` or `--direction` on project/namespace is meaningless and is a usage error (64).

The default neighbourhood depth is 1, not the measured 2 or 3: the table shows depth 2 of a hub is already 25k–59k tokens, and a diagram nobody can read is worse than three small ones.

### 2. Edge kinds and roles drawn by default

Draw the **flow** kinds: `Call`, `Interface`, `Override`, `Mediatr`, `DiBinding`, `Construct`, `Route`. Exclude `TypeUse` by default. `TypeUse` is ownership, not execution — the same reason `WhereWeigh` skips it (`src/CsMesh/Analysis/Queries.where.cs:226`) and `BlastRadius` skips it (`Queries.cs:302`) — and it is the single largest kind in both graphs (2397/8566 and 1453/5328, ~28%). A `--all-edges` flag (or `--include typeuse`) can add it; the default is the executable graph.

Edge `Role` (`src/CsMesh/Models/Edge.cs:38-49`) does not create separate edges. A `Call` carrying `Write` is still one arrow; the role is available to be shown as a style (a dashed arrow) or ignored, and the default is to draw it dashed. `Read|Write` is flags, so the test is bitwise, never equality.

### 3. Confidence below 0.80 and `{test}` code

- **Confidence < 0.80** (`Edge.Score`, threshold `Edge.cs:56`) is a guess, and the convention everywhere else in csmesh is to say so rather than hide it: rows carry `?score`. Export draws such an edge **dashed with a `?0.xx` label** rather than excluding it. Neither measured graph has one, so this rule costs nothing today and states the contract for the graphs that do.
- **`{test}` nodes** (`Node.Tags` contains `test`) are **excluded by default**, the way `blast-radius` separates production callers from tests (`Queries.cs:350-351`). The summary line states how many nodes and edges were withheld, so the omission is visible. `--include-tests` adds them.

### 4. Deterministic order, ids and labels

Rendered output must be byte-identical across two `--full` indexes of an unchanged tree. That rules out, as the source of an id:

- `Node.Id` — positional/insertion-order, not stable across edits (`Node.cs:8,11-20`);
- dictionary/HashSet enumeration order, parallel-`For` completion order, timestamps, `Guid`s;
- the key text itself, which has unsafe characters and, for top-level programs, an absolute path.

**Rank ids did not survive review.** Numbering nodes `p0…`/`ns0…`/`s0…` by their rank in `Node.Key` order looks stable — two indexes of one tree agree — but a rank is positional in a sorted list: add one symbol and every later rank shifts. A diagram committed to a design doc would then diff in full on a change that touched nothing in the picture.

The scheme is a content hash. A node id is a level letter prefix — `p` project, `ns` namespace, `s` symbol — plus the first 8 hex digits of SHA-256 of the node's identity text: the **project path** for a project node, the **namespace name** for a namespace node, and `Node.Key` for a symbol. Hashing the identity rather than the position means an added symbol changes only its own line. On a collision within one export — two identities sharing 8 hex digits — the colliding ids extend to 12, then 16 digits, deterministically, until they separate; an id that collides with nothing else in the render keeps its 8-digit form. Edges sort by `(fromKey, toKey, kind)`, and `Graph.IdForKey` (`Graph.cs:348`) is the lookup from key to node. Ids are stable per graph; two indexes of one tree produce the same ids. They are not portable across two checkouts of different paths, because a top-level program's `Node.Key` embeds the absolute path — so **labels never use the key**, only the hash-derived id and the display name.

Labels:

- project → the project path; namespace → the namespace; both quoted.
- symbol → `Node.Short`, and for an overloaded name the **selector form** `Type.Member(int,string)` that exit 3 already prints (`SymbolSelector.SelectorFor`, `SymbolSelector.cs:295`), so a diagram and a query speak the same name. Duplicate `Short` values across types are distinguished by the id, not by lengthening the label.

### 5. Budget — truncate to stdout, or write a file with `--out`

All answer text goes through `BudgetWriter`. The measurement settles the shape: the project level fits any sensible budget, but the namespace level and a hub's depth-1 neighbourhood do not. The private solution's namespace render is **2501 Mermaid tokens / 3111 DOT tokens** (the table above); CsMesh's top-degree symbol is **5412 / 6857 tokens at depth 1** and **10913 / 14017 at depth 2**, the private solution's **1990 / 2496** then **15670 / 20226**. Only `--level project` fits a default budget; nothing coarser does.

Two options were rejected each on its own:

- **A. Aggregate to a coarser level.** When the chosen level exceeds the budget, render the next level up (neighbourhood → namespace → project) or a smaller depth, and say on the first line which level was actually drawn. Rejected: the caller asked for one level and got another, silently, and the measured namespace and neighbourhood sizes above make the trigger common rather than exceptional.
- **B. Truncate and exit 2.** Emit nodes and edges in the section's deterministic order until `BudgetWriter.Add` refuses, set `Overflowed`, print the `INCOMPLETE` marker, exit 2 — exactly `trace`'s contract (`Queries.cs:155-164`). Rejected on its own because a partial diagram is still shaped like a graph and can mislead more than an empty one, and because a caller who wants the whole picture has no way to get it.

The decision is **B + C**: B on stdout, C when `--out` is given, so the two deficiencies above cancel.

- **Without `--out`.** The rendering goes to stdout through `BudgetWriter`. Overflow sets `Overflowed`, exit 2, and the note names both remedies: `--out <file>` for the complete render, and a coarser `--level` (or a smaller `--depth`, at the neighbourhood level) to fit the budget.
- **With `--out FILE`.** The complete render is written to `FILE`, and stdout carries only a budgeted summary: the path, the format, the level, node and edge counts, and the withheld counts (test code, `TypeUse`, beyond `--depth`). The write is temp file + rename, as `GraphStore` does, so a reader never sees a half-written file. The file is an **artifact, not answer text**: it reaches an agent's context only if the agent reads it, so the budget contract — which bounds stdout — still holds.
- **MCP.** `McpTools` forwards a tool call to the same CLI command and **captures stdout** (`src/CsMesh/Mcp/McpTools.cs:246-300`); it never moves files. MCP forwards `--out` unchanged and returns the summary, which is the only signal the caller gets.
- **`--out` paths.** `--out` must resolve under the repository root; a path outside it, or one whose parent directory does not exist, is exit 64. A write that fails for any other reason (permissions, I/O) is exit 70.

### 6. Neighbourhood traversal

A small ring walk: frontier starts at the resolved node, and each level visits `g.Out`, `g.In`, or both, according to `--direction in|out|both` (**default `both`**). It dedups with a visited set keyed by `Node.Id` (valid within one render), skips `TypeUse` per decision 2, and stops at `--depth` (**default 1**). The default depth is 1 because the measured growth is steep: the private solution's top symbol is 1990 Mermaid tokens at depth 1 and 15670 at depth 2, CsMesh's 5412 and 10913 — a depth the caller widens deliberately, not one to default into. This is new code but a few dozen lines; it reuses the graph adjacency, the visited-set pattern, and `BudgetWriter`. It does not reuse `Trace` or `BlastRadius` because both are directed and filtered.

### 7. Exit codes

Existing codes only; no new one (`src/CsMesh/Common/ExitCodes.cs`):

- `0` rendered; `2` over budget (stdout truncation); `4` no usable index; `64` bad `--format`, bad `--level`, bad `--direction`, `--depth`/`--direction` on a level that ignores it, or `--out` outside the repository root or under a missing parent; `70` an I/O failure while writing `--out`; `1` a symbol that resolves to nothing; `3` a symbol that resolves to several nodes without a selector.

### 8. Format version

**No bump.** Export reads `Node.Key`, `Node.Tags`, and `Edge.Kind/Role/Score` and writes text to stdout (or a file). It changes no node keying, no edge semantics and no on-disk shape; `Graph.CurrentFormatVersion` stays v14. A file written by `--out` is an output artifact, never read back.

### 9. Out of scope

- Layout hints, coordinates, or any attempt to make the picture pretty; the renderer emits nodes and edges and leaves layout to Mermaid/DOT.
- Clustering beyond the project and namespace levels.
- Styling themes, colours, per-kind palettes.
- Image rendering (`svg`/`png`); Mermaid and DOT text only, so the output is diffable and budgetable.
- Filtering by tag or by an arbitrary node predicate beyond `--include typeuse` / `--include-tests` and the kind defaults.

## Consequences

- `export` is a read-only renderer: no graph field, no format bump, no new exit code.
- Determinism rests on `Node.Key` as the identity hashed into every id, which is already the identity every other command uses; a change to keying would need a format bump regardless, so export inherits that gate.
- The budget decision is B + C: stdout truncates and exits 2 with both remedies named, and `--out` writes the complete render as a temp-file-and-rename artifact with a budgeted summary on stdout, so a real namespace or hub never silently becomes a different picture.
- Mermaid and DOT are text, so both are budgeted and diffable by the same machinery as every other answer; `--out` is the one path whose body leaves the answer stream, and only its summary is budgeted.
