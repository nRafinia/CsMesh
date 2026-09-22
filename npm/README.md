<div align="center">

# ⚡ csmesh

### Structural Code Intelligence Engine for C# & .NET

**Answers architectural, call-graph, and dependency questions under a hard token budget.**  
Built for AI coding agents and developers who are tired of multi-turn "file-hopping" and noisy grep queries.

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native-AOT%20Ready-success?logo=speedtest&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
[![Token Reduction](https://img.shields.io/badge/Context%20Spend--93%25-brightgreen)](https://github.com/nRafinia/CsMesh)
[![Latency](https://img.shields.io/badge/Query%20Latency-~28ms%20NativeAOT-blue)](https://github.com/nRafinia/CsMesh)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

</div>

![logo](pics/readme.jpg)


---

```bash
$ csmesh trace PaymentController.Post --budget 600

PaymentController.Post  {http:POST /charge}  Api/PaymentController.cs:14
  -> CreatePaymentCommandHandler.Handle  [mediatr via Send(CreatePaymentCommand)]  App/CreatePaymentCommand.cs:18
    -> IPaymentGateway.Authorize  Infra/Repositories.cs:11
      -> StripeGateway.Authorize  [impl, di-bound]  Infra/Repositories.cs:29
    -> IPaymentRepository.Add  Infra/Repositories.cs:7
      -> PaymentRepository.Add  [impl, di-bound]  Infra/Repositories.cs:17
        -> AppDbContext.SavePayment  Infra/Repositories.cs:34
      -> InMemoryPaymentRepository.Add  [impl]  Infra/Repositories.cs:23
```


---

## 📑 Table of Contents

- [The Problem: The Hidden Tax of AI Code Exploration](#-the-problem-the-hidden-tax-of-ai-code-exploration)
- [Dual Mode: CLI and MCP Server Support](#-dual-mode-cli-and-mcp-server-support)
- [Empirical Benchmarks](#-empirical-benchmarks)
- [Key Features](#-key-features)
- [Installation](#-installation)
  - [Global .NET Tool](#1-as-a-global-net-tool)
  - [Standalone Native AOT Binary](#2-as-a-standalone-native-aot-binary-zero-runtime-dependency)
- [Quick Start](#-quick-start)
- [Supported IDEs & AI Coding Agents](#-supported-ides--ai-coding-agents)
- [CLI Reference](#-cli-reference)
- [The Recommended Hybrid Workflow: csmesh vs. grep](#-the-recommended-hybrid-workflow-csmesh-vs-grep)
- [Deterministic Exit Codes](#-deterministic-exit-codes)
- [Telemetry & Audit Logging](#-telemetry--audit-logging)
- [Repository Structure](#-repository-structure)
- [License](#-license)

---

## 🛑 The Problem: The Hidden Tax of AI Code Exploration

When an AI coding agent or engineer asks a structural question about a decoupled C# codebase—such as:
* *"If I modify this interface method, what breaks downstream?"*
* *"Which concrete class is actually resolved and injected by the DI container at runtime?"*
* *"Where does this Mediator `_mediator.Send()` or background queue message end up?"*

...standard tooling forces a repetitive, expensive **"file-hopping" loop**:

```
[Agent] grep for interface -> finds 12 test fakes, mocks, & docstrings
   ↓ (Turn 1: ~600 tokens)
[Agent] opens File A -> finds interface dispatch, not the implementation
   ↓ (Turn 2: ~800 tokens)
[Agent] greps for DI registration -> sifts through Program.cs and test fixtures
   ↓ (Turn 3: ~1,200 tokens)
[Agent] opens File B -> discovers it sends a MediatR command
   ↓ (Turn 4: ~1,500 tokens)
Context Exhaustion & Slow Responses (~5,000 tokens burned, 4-5 turns wasted)
```

In layered, enterprise .NET applications, **lexical text search (`grep`, `ripgrep`) hits a wall**. Text search cannot see:
1. **Dependency Injection Bindings** (`AddScoped<IService, Service>()`)
2. **CQRS / MediatR Handler Dispatches** (`_mediator.Send(cmd)`)
3. **Interface Implementation Ranking** (distinguishing production services from mock fakes)
4. **Attribute-based & Blazor Endpoint Routing** (`[HttpGet]`, `[HttpPost]`, `@page "/..."`)
5. **Razor & Blazor Components** (types, `@code` methods, and parameter bindings compiled via Roslyn source generators)

**`csmesh` solves this in a single shell command.** It parses your codebase's AST and semantic model via Roslyn into a pre-compiled, frozen symbol graph that returns exact answers in milliseconds.

---

## 🔌 Dual Mode: CLI and MCP Server Support

`csmesh` supports both **command-line interface (CLI)** and **Model Context Protocol (MCP)** workflows with a first-class experience, giving developers and AI agents the flexibility to choose what works best:

* **⚡ Fast CLI-First Execution:**
  - **Zero Idle Context Overhead:** Incurs zero token spend until explicitly invoked.
  - **Hard Token Caps (`--budget`):** Guarantees answers fit within strict limits (e.g. `--budget 300` or `--budget 600`), exiting cleanly with code `2` on overflow instead of polluting conversation history.
  - **Command Chaining:** Chain queries in a single turn (`csmesh impl IStore --budget 200 && csmesh blast-radius Order.Submit --budget 400`).

* **🤖 Native Model Context Protocol (MCP) Server:**
  - **Interactive Agent Experience:** Run `csmesh serve` to expose query tools over standard JSON-RPC (stdio) to MCP-compatible clients like Claude Desktop, Cursor, Antigravity, VS Code, Windsurf, and Cline.
  - **One-Command Setup:** Register csmesh into IDE configurations with `csmesh install --mcp` (locally) or `csmesh install --mcp --global` (machine-wide).
  - **Token-Efficient Tool Responses:** Returns dense, structured plaintext designed for LLM comprehension without wasteful, unbounded JSON dumps.

---

## 📊 Empirical Benchmarks

To quantify the real-world performance gains, `csmesh` was benchmarked against the standard AI agent workflow (**Ripgrep / `rg` + sequential file reads**) across a private 29-project .NET solution (**1,945 symbols, 5,164 edges**, measured 2026-09-21 on csmesh 0.6.0).

The evaluation measured four critical dimensions:
1. **Query & Execution Latency**: Raw tool execution time and total agent turnaround time.
2. **Agent Round-Trips**: Number of iterative tool calls and model reasoning turns required to reach the answer.
3. **Context Spend & Token Consumption**: Prompt tokens spent on search noise vs. dense architectural facts.
4. **Semantic Accuracy**: Ability to distinguish runtime DI bindings, production code, and test doubles.

---

### Scenario-by-Scenario Benchmark Summary

| Workflow Scenario | Command | Median (min–max) | csmesh tokens | Baseline | Reduction |
|:---|:---|---:|---:|:---|---:|
| **1. DI Implementation & Binding** | `csmesh impl IOrderRepository` | 27.6 ms (26.9–43.2) | 97 | ~1,500 tokens via grep + read | ~94% |
| **2. Deep Call Chain Trace** | `csmesh trace OrderEndpoints.CreateOrderAsync --depth 2` | 27.8 ms (26.6–29.9) | 288 | ~4,000 tokens via 5–7 turns | ~93% |
| **3. Change Impact & Blast Radius** | `csmesh blast-radius OrderService.UpdateAsync` | 27.4 ms (26.7–29.6) | 105 | 4–6 turns over 24+ noisy grep hits (no token baseline) | n/a |
| **4. Multi-Hop Path Finding** | `csmesh path OrderEndpoints.CreateOrderAsync OrderWriter` | 29.7 ms (27.4–45.4) | 115 | impossible with grep (no token baseline) | n/a |
| **5. Endpoint & Worker Discovery** | `csmesh entrypoints` | 24.8 ms (23.5–32.3) | 738 | repeated greps + manual parsing (no token baseline) | n/a |
| **6. Type Structure & Signature** | `csmesh context OrderRecord` | 27.7 ms (26.5–30.5) | 478 | locate and read the whole file (no token baseline) | n/a |
| **7. Full Architecture Mapping** | `csmesh map` | 28.8 ms (27.5–41.6) | 766 | read `.slnx` + 29 `.csproj` files (no token baseline) | n/a |

Symbol names above are placeholders; the numbers were measured on the real codebase with symbols of the same shape. Median of 20 warm runs (graph pre-indexed and held in the OS file cache) on a NativeAOT win-x64 build of csmesh 0.6.0, 2026-09-21, on a 13th Gen Intel Core i7-13620H (10 cores / 16 threads, 48 GB RAM, NVMe SSD). Token counts use csmesh's own estimator at roughly four characters per token.

---

### Deep-Dive Real-World Scenarios

Times below are the same 2026-09-21 medians as the table above, measured on the **NativeAOT win-x64** channel; the framework-dependent `dotnet tool` channel pays JIT warm-up and is slower.

#### 1. Interface Implementation & Runtime DI Resolution
* **With `csmesh impl IOrderRepository --budget 300` (27.6 ms, NativeAOT win-x64):**
  Identifies all 3 concrete implementations in a single glance: tags `SqlOrderRepository` with `[di:scoped]` along with the exact file and line where it was bound in the IoC container, while clearly marking `SpyOrderRepository` and `InMemoryOrderRepository` as test doubles.
* **Without `csmesh`:**
  - *Turn 1:* Run `rg ":\s*IOrderRepository\b"` to find inheriting classes (returns multiple classes, but cannot indicate which one is registered in production).
  - *Turn 2:* Run `rg "AddScoped.*IOrderRepository"` to discover registration logic.
  - *Turn 3:* Open the DI module or test fixture to verify which instance actually executes at runtime.

#### 2. Forward Call Chain Tracing Across Interface Boundaries
* **With `csmesh trace OrderEndpoints.CreateOrderAsync --depth 2` (27.8 ms, NativeAOT win-x64):**
  Follows execution seamlessly across decoupled interface abstractions. Traces `IAuthorizationService.AuthorizeAsync` directly to its concrete implementation `AuthorizationService.AuthorizeAsync`, continuing downstream to `AuditLogger.LogAsync` and `AppDbContext.SaveChangesAsync`.
* **Without `csmesh`:**
  The agent must open the endpoint file (~100 lines), observe the interface call, search for the interface declaration, grep for implementations, open the implementation source, and repeat this cycle until reaching the persistence layer—burning 5 to 7 turns and 30+ seconds of reasoning time.

#### 3. Blast Radius & Change Impact Analysis
* **With `csmesh blast-radius OrderRepository.UpdateAsync --budget 800` (27.4 ms, NativeAOT win-x64):**
  Computes the reverse transitive dependency graph: reveals that modifying `UpdateAsync` impacts 18 internal members, 1 public HTTP route (`OrderEndpoints.CreateOrderAsync`), and 14 tests across 4 separate projects—clearly categorizing test callers vs. production entrypoints.
* **Without `csmesh`:**
  Running `rg "\bUpdateAsync\b"` returns dozens of raw matching lines across interfaces, mocks, comments, and unrelated classes. Text search cannot determine which root endpoints ultimately depend on this method without exhaustive manual back-tracing.

#### 4. Multi-Hop Path Finding
* **With `csmesh path OrderEndpoints.CreateOrderAsync OrderRepository.UpdateAsync` (29.7 ms, NativeAOT win-x64):**
  ```text
  OrderEndpoints.CreateOrderAsync
    -> OrderService.ProcessOrderAsync
      -> IOrderRepository.UpdateAsync
        -> SqlOrderRepository.UpdateAsync [impl, di-bound]
  ```
  Resolves the exact 4-hop invocation path through services and DI container registrations in 29.7 ms—a task fundamentally beyond the capabilities of text search.

---

### Core Takeaways

1. **Semantic Intelligence vs. Lexical Speed:** While `ripgrep` searches text in 30–50 ms, its output is **lexical, not semantic**. `csmesh` answers in **~25–30 ms** (median of 20 warm runs on the NativeAOT win-x64 channel, measured 2026-09-21 on csmesh 0.6.0), but returns definitive, actionable architectural conclusions rather than raw strings.
2. **Eliminating the Agent Turn Latency Bottleneck:** In AI agent interactions, the dominant latency cost is LLM inference and reasoning per turn (often 5–15 seconds per round-trip). By collapsing 4 to 8 file-hunting turns into **1 single shell command**, `csmesh` cuts total agent task completion time by **over 80%**.
3. **Context Window Hygiene:** Replacing full source file dumps with compact graph edges saves **~93–94% of token spend** in the two measured scenarios that have a token baseline (DI binding and call-chain trace), preserving the model's context window for actual implementation rather than navigation. The other scenarios were measured as csmesh token counts only, against qualitative baselines.

---

## ✨ Key Features

- **🚀 Native AOT & .NET 10 Ready:** No JIT warm-up, no runtime to install, and lookup tables frozen once at load (`System.Collections.Frozen`) so a query walks the graph instead of rebuilding indexes. Median query on the measured 29-project solution: ~28 ms (median of 20 warm runs on the NativeAOT win-x64 channel, csmesh 0.6.0, 2026-09-21).
- **🎨 Blazor & Razor Component Intelligence:** Indexes Blazor components, `@code` methods, component parameters, and Razor Pages / MVC views. Automatically discovers `@page "/..."` routes as HTTP entrypoints and accurately maps line numbers back to `.razor` and `.cshtml` source files via Roslyn `#line` directives.
- **🛡️ Token-Budget Enforcement (`--budget N`):** Hard limits on output tokens. Prevents agent context exhaustion by exiting with actionable tips when a query is too broad.
- **💉 DI & IoC Container Intelligence:** Reads service registrations in every form they take — two-argument, `typeof` pairs, keyed, factory lambdas, and alias registrations such as `sp => sp.GetRequiredService<Concrete>()` — and ranks the class the container actually returns ahead of the ones nobody registered.
- **📨 MediatR & CQRS Linking:** Resolves `_mediator.Send(...)` and `Publish(...)` calls to their concrete request handlers across decoupled project boundaries.
- **💥 Blast Radius & Impact Analysis:** Computes the reverse call graph to surface all direct/indirect callers, affected controllers, and background consumers before modifying a symbol.
- **🧩 Nested Type & Member Resolution:** Resolves members declared inside nested types seamlessly (e.g. `Container.Compute` automatically resolves `Container.Inner.Compute`), avoiding lookup misses without shadowing direct matches.
- **🌐 Universal AI Agent Integration:** Installs native prompt rules and skills for **12+ AI tools** (Claude Code, Cursor, Antigravity, OpenCode, Windsurf, Cline, Copilot, MiMo Code, etc.) with both local and `--global` machine-wide support.
- **🔄 Incremental Re-indexing:** Node identity is a compiler symbol key, not an array position, so an edit re-binds only the files that moved and every edge into them survives. Rows from files the index has not caught up with are tagged `[STALE]`; `--heal` re-binds them before answering. Falls back to a full pass when an edit touches something that binds across files.
- **🧭 Entry by Description, Not by Name:** `csmesh where <term>` searches names, namespaces, file paths and route templates, then ranks by how many entrypoints reach each hit — so the handler outranks the DTO that shares its name.

---

## 📦 Installation

### ⚡ Automatic One-Line Install (Recommended)

**Linux & macOS:**
```bash
curl -fsSL https://raw.githubusercontent.com/nRafinia/CsMesh/main/install.sh | sh
```

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/nRafinia/CsMesh/main/install.ps1 | iex
```

The install scripts download the prebuilt Native AOT binary for your OS and architecture — `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` — and fall back to the .NET global tool when no asset matches.

---

### 1. As a Global .NET Tool

```bash
# Install from NuGet.org
dotnet tool install --global CsMesh

# Or build and install locally from source
dotnet pack -c Release
dotnet tool install --global --add-source ./src/CsMesh/bin/Release CsMesh

# Or update an existing installation
dotnet tool update --global CsMesh
```

From 0.6.1 the tool package is RID-specific: on `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64` and `osx-x64`, `dotnet tool install` fetches the Native AOT binary. On any other platform it falls back to the framework-dependent build.

### 2. Via npm / npx
```bash
Bash
# Run directly without global installation
npx @nrafinia/csmesh --help

# Or install globally across Windows, macOS, and Linux
npm install -g @nrafinia/csmesh
```

`npx` and `npm install -g` download the prebuilt Native AOT binary for your platform (the six RIDs listed above) from the GitHub release, so no .NET runtime is needed.

### 3. As a Standalone Native AOT Binary (Zero Runtime Dependency)

You can compile a single, standalone binary with zero dependencies on the .NET SDK:

```bash
# Windows (win-x64)
dotnet publish src/CsMesh/CsMesh.csproj -c Release -r win-x64 -p:PublishAot=true

# Linux (linux-x64) - run via Linux or WSL
dotnet publish src/CsMesh/CsMesh.csproj -c Release -r linux-x64 -p:PublishAot=true

# macOS (osx-arm64)
dotnet publish src/CsMesh/CsMesh.csproj -c Release -r osx-arm64 -p:PublishAot=true
```

The resulting binary in `bin/Release/net10.0/<rid>/publish/` runs on machines without .NET installed and, on the measured NativeAOT win-x64 channel, answers a warm query in ~28 ms median (csmesh 0.6.0, 2026-09-21).

---

## 🚀 Quick Start

Run these commands inside any C# / .NET repository (`.sln`, `.slnx`, `.csproj`):

### 1. Index the Repository
```bash
csmesh index
# indexed 28 files -> 161 nodes, 380 edges in 0.1s
```

> [!TIP]
> **Using source generators — or starting from a cold checkout?** A fresh clone resolves less
> completely than a built one: package and framework types come from `bin/`, and generated sources
> (Blazor/Razor, System.Text.Json, `[GeneratedRegex]`, `[LibraryImport]`, `[LoggerMessage]`, ...)
> live under `obj/`. A plain build restores the references; the flags additionally write the
> generated sources to disk where `csmesh` can read them:
> ```bash
> dotnet build --no-incremental -p:EmitCompilerGeneratedFiles=true
> csmesh index
> ```
> `csmesh doctor` reports what it found, and marks unresolved calls when it could not see them.

### 2. Configure Your AI Coding Assistants
```bash
# Install skill and rule files in the current repository:
csmesh install

# Or install both skill files and MCP server integration:
csmesh install --all

# Or install machine-wide into your user profile (~/.claude, ~/.cursor, ~/.gemini, etc.):
csmesh install --global
```

### 3. Ask Structural Questions
```bash
# I have words, not a symbol name.
csmesh where discount

# What does this method call down the line?
csmesh trace OrderService.SubmitOrder --budget 600

# Which concrete implementation runs for this interface in DI?
csmesh impl IPaymentGateway --budget 600

# What breaks if I change this method or property?
csmesh blast-radius Order.Status --budget 800

# Where are all the API routes and hosted workers?
csmesh entrypoints orders
```

---

## 🤖 Supported IDEs & AI Coding Agents

`csmesh install` sets up native prompt instructions and skills across all major coding tools:

| Agent / IDE | Local Target (`csmesh install`) | Global Target (`--global` / `-g`) | Format |
|:---|:---|:---|:---|
| **VS Code** | `.vscode/mcp.json` + `.github/copilot-instructions.md` | `~/.copilot/copilot-instructions.md` | MCP Server + Copilot Instructions |
| **JetBrains Rider** | `.mcp.json` + `AGENTS.md` | `~/.ai/mcp/mcp.json` + `~/.codex/AGENTS.md` | MCP Server + Agent Rules Block |
| **Claude Code** | `.claude/skills/csmesh/SKILL.md` | `~/.claude/skills/...` + `CLAUDE.md` | Skill (YAML frontmatter) |
| **Cursor** | `.cursor/rules/csmesh.mdc` + `.cursor/mcp.json` | `~/.cursor/rules/...` + `~/.cursor/mcp.json` | MDC Rule + MCP Server |
| **Google Antigravity** | `.agents/skills/csmesh/SKILL.md` + `.agents/mcp_config.json` | `~/.gemini/config/skills/...` + `mcp_config.json` | Workspace Skill + Rules + MCP |
| **Windsurf (Cascade)** | `.windsurfrules` | `~/.codeium/windsurf/` (rules & `mcp_config.json`) | Tagged Rules Block + MCP Server |
| **Cline & Roo Code** | `.clinerules` or `.cline/mcp.json` | `~/.cline/rules/` + `cline_mcp_settings.json` | Tagged Instruction Block + MCP Server |
| **GitHub Copilot** | `.github/copilot-instructions.md` | `~/.copilot/copilot-instructions.md` | User Instructions Block |
| **MiMo Code (Xiaomi)** | `.mimocode/skills/csmesh/SKILL.md` + `AGENTS.md` | `~/.mimocode/skills/...` + `.mimo/` | Skill + Agent Instructions |
| **Kilo Code** | `.kilocode/rules/csmesh.md` | `~/.kilocode/rules/csmesh.md` | Native Rule File |
| **Codex CLI & Kimi AI**| `AGENTS.md` | `~/.codex/AGENTS.md` | Open Agent Standard Block |
| **Gemini CLI** | `GEMINI.md` | `~/.gemini/GEMINI.md` | Open Agent Standard Block |
| **OpenCode** | `.opencode/rules/` + `commands/` + `opencode.json` | `~/.config/opencode/` (`AGENTS.md`, `commands/`, `opencode.json`) | Rules + Slash Commands + Native MCP |

> [!TIP]
> Shared configuration files (`AGENTS.md`, `GEMINI.md`, `.windsurfrules`, `.clinerules`, `.github/copilot-instructions.md`) use safe tagged blocks (`<!-- csmesh-instructions -->`). Existing developer rules are **never overwritten**. When `--mcp` is passed (`csmesh install --mcp` or `csmesh install --all`), native MCP server configurations (`.mcp.json`, `.vscode/mcp.json`, `.cursor/mcp.json`, `~/.claude.json`, `cline_mcp_settings.json`, etc.) are also automatically merged.

---

## 📖 CLI Reference

### Global Options

| Option | Description |
|:---|:---|
| `--repo <PATH>` | Target repository root (default: nearest `.sln`, `.slnx`, or `.git` above cwd) |
| `--under <PATH>` | Restrict the answer to a subtree, e.g. `--under src/Api`. Narrow before raising the budget. |
| `--project <PATH>` | Pick one project when a name repeats across assemblies, e.g. `--project src/Api`. Exit `3` lists each candidate's project. |
| `--budget <N>` | Hard token limit for stdout. Exits code `2` on overflow. Defaults per command below. |
| `--depth <N>` | Traversal depth limit (`trace` 6, `blast-radius` 3, `context` 3, `path` 12, `diff` 3) |
| `--heal` | Re-bind changed files before answering, instead of marking rows `[STALE]` |
| `--json` | Output results in structured JSON format |
| `--debug` | Print verbose diagnostics to stderr |
| `--no-telemetry` | Skip recording the invocation in local usage metrics |
| `-h, --help` | Display command help and usage examples |

Default budgets: `impl` 600, `path` 500, `where`/`trace` 600, `unresolved` 700, `silence` 300, `entrypoints` 800, `map` 850, `context` 900, everything else 800.

---

### Commands

#### `csmesh map`
Where the weight is: which projects lean on which, where the entrypoints cluster, and the handful of members everything runs through. The first command to run in a repository you do not know — `ls` answers "where are the files", which is the wrong axis.
```bash
csmesh map
csmesh map --under src/Application --budget 400
```

#### `csmesh where <term>` (alias: `find`)
Finds the symbols a word belongs to, ranked by how many entrypoints reach them. Start here when the task is described in words rather than symbol names; the last line is the next command, already filled in.
```bash
csmesh where discount
csmesh where checkout refund --under src/Application
csmesh find "POST /orders"
```

#### `csmesh index`
Builds or refreshes the Roslyn symbol graph stored in `.csmesh/graph.json`. Incremental by default: only the files that changed since the last index are re-bound, and their symbols keep their existing identity so every edge into them survives the edit. Falls back to a full pass when an edit touches something that binds across files — an interface declaration, a handler, a container registration. The `.csmesh/` directory is added to `.git/info/exclude` when it is created, so it never shows up as untracked in a work repository.
```bash
csmesh index
csmesh index --full          # force a full re-index of every in-scope project
csmesh index --all           # include projects no solution file builds
csmesh index --repo ./src
```

#### `csmesh trace <Type.Member>`
Follows execution pathways through method calls, interface dispatch, MediatR, and constructor invocations.
```bash
csmesh trace PaymentController.Post --budget 600
csmesh trace OrderService.Submit --depth 3
```

#### `csmesh impl <IInterface>`
Finds all implementations of an interface, ranking DI-bound registrations first.
```bash
csmesh impl IPaymentGateway --budget 600
csmesh impl IOrderRepository
```

#### `csmesh blast-radius <Type.Member>` (alias: `blast`)
Discovers direct callers, transitive callers, and reachable entrypoints affected by modifying a member.
```bash
csmesh blast-radius Order.Status --budget 800
csmesh blast PaymentService.Process --depth 2
```

#### `csmesh entrypoints [filter]`
Finds HTTP endpoints (`[HttpGet]`, `[HttpPost]`, Blazor `@page`), message handlers, consumers, and background services.
```bash
csmesh entrypoints
csmesh entrypoints payments
csmesh entrypoints "POST /orders"
```

#### `csmesh context <Type.Member>`
Everything structural about one symbol in a single call: signature, members, callers, callees, implementations and the entrypoints above it. Replaces a `trace` plus an `impl` plus a `blast-radius`.
```bash
csmesh context OrderService --budget 900
csmesh context IPaymentGateway.Authorize --depth 2
```

#### `csmesh path <From> <To>` (alias: `why`)
The shortest route between two symbols, across DI bindings and MediatR dispatch. Answers "how does this controller ever reach that repository".
```bash
csmesh path PaymentController.Post SqlOrderStore.Save
csmesh why OrderController.Post CreateOrderHandler.Handle --budget 500
```

#### `csmesh cycles`
Circular dependencies between types, namespaces or projects. Reports one concrete loop per component rather than an unordered set.
```bash
csmesh cycles
csmesh cycles --project
csmesh cycles --namespace --under src/Domain
```

#### `csmesh diff [ref]`
The symbols a git change touched, and what they reach. Defaults to the working tree against `HEAD`.
```bash
csmesh diff
csmesh diff --staged
csmesh diff origin/main --budget 800
```

#### `csmesh changes`
Bindings, dispatches and implementations that appeared or vanished since the previous index — the structural change, not the textual one. Warns when a DI binding or a MediatR dispatch no longer resolves, which the compiler will not catch and mocked unit tests will not fail on.
```bash
csmesh changes
csmesh changes --calls --budget 1200
```

#### `csmesh review [base]`
The same structural comparison as `changes`, but against a named git revision instead of whatever the last index happened to see — the question a pull request or a CI gate actually asks. Defaults to the merge base with the remote's default branch, cached per commit so a second run is fast. `--accept` writes the current findings to `.csmesh/accepted.txt`; accepted findings stop being reported, and dead entries are pruned automatically once the base moves past them. Exits `5` when something unaccepted remains, so a pipeline can gate on it without parsing prose. When the index predates `HEAD` the comparison is refused rather than guessed at — exit `4`, or `64` for `--accept`, since findings from the wrong current side must not be written to the baseline.
```bash
csmesh review                        # vs. the merge base with the default branch
csmesh review origin/main --calls
csmesh review --accept               # bless the current state as the new baseline
```

#### `csmesh silence <symbol> [<target>]` (alias: `why-not`)
Why a query came back empty. Exit `1` from any other command means the graph had nothing; it does not say whether the symbol was mistyped, lives in a package, was never bound because the solution was not built, or is reached only through a container scan. Those call for four different next actions.
```bash
csmesh silence IPaymentGateway
csmesh why-not OrderController.Post SqlOrderStore.Save
```

#### `csmesh unresolved`
Where the indexer failed, grouped by reason. Run this when an answer is thinner than expected.
```bash
csmesh unresolved
csmesh unresolved --kind di
```

#### `csmesh usage`
Displays local invocation analytics, token spend, caller attribution, and latency percentiles.
```bash
csmesh usage           # Summary for last 7 days
csmesh usage --days 30 # Summary for last 30 days
csmesh usage --tail 10 # Last 10 raw invocations
```

#### `csmesh doctor`
Diagnoses index freshness, dirty files, caller attribution, and agent skill configurations.
```bash
csmesh doctor
```

#### `csmesh install [OPTIONS]`
Installs agent skill and rule files and optional MCP server integrations for AI assistants.
```bash
csmesh install                      # Install skill and rule files for current repo
csmesh install --mcp                # Register csmesh as an MCP server
csmesh install --all                # Install skills and MCP server
csmesh install --global             # Install globally across all user agents
csmesh install -g --agent cursor   # Install globally for Cursor only
```

#### `csmesh uninstall [OPTIONS]`
Safely removes agent skill/rule files, cleans up generated blocks, and unregisters MCP server integrations.
```bash
csmesh uninstall                    # Remove skill and rule files from current repo
csmesh uninstall --mcp              # Unregister MCP server
csmesh uninstall --all              # Remove skills and MCP server
csmesh uninstall --global           # Remove globally across user config
csmesh uninstall -g --agent cursor # Remove globally for Cursor only
```

#### `csmesh serve`
Exposes csmesh query tools to AI agents as a Model Context Protocol (MCP) server over stdio.
```bash
csmesh serve
csmesh serve --repo ./src
```

---

## ⚖️ The Recommended Hybrid Workflow: csmesh vs. grep

A symbol graph is not a replacement for text search or reading code; it is a replacement for **blindly hunting for code**. The most effective engineers and agents combine both tools:

```
                  ┌─────────────────────────────────────┐
                  │          What are you asking?       │
                  └──────────────────┬──────────────────┘
                                     │
           ┌─────────────────────────┴─────────────────────────┐
           ▼                                                   ▼
┌──────────────────────┐                           ┌──────────────────────┐
│ Structural / Graph   │                           │  Lexical / Textual   │
├──────────────────────┤                           ├──────────────────────┤
│ • Call hierarchies   │                           │ • String literals    │
│ • Who calls whom     │                           │ • Error messages     │
│ • Interface dispatch │                           │ • Config keys & YAML │
│ • Impact analysis    │                           │ • Docker / scripts   │
│ • Entrypoint routing │                           │ • Enum constants     │
└──────────┬───────────┘                           └──────────┬───────────┘
           ▼                                                   ▼
       csmesh                                            grep / ripgrep
           │                                                   │
           └─────────────────────────┬─────────────────────────┘
                                     ▼
                    ┌─────────────────────────────────┐
                    │      Direct File Inspection     │
                    │ (Evaluate if/else, error flow)  │
                    └─────────────────────────────────┘
```

| Task | Primary Tool | Why? |
|:---|:---|:---|
| **Impact / Blast Radius** | `csmesh blast-radius` | Zero noise; eliminates candidate fakes and mocks across the solution. |
| **Interface Implementations** | `csmesh impl` | Resolves runtime DI bindings (`[di:bound]`) instantly. |
| **Execution Call Traces** | `csmesh trace` | Collapses multi-hop file reads into a single 5-line tree. |
| **Error Messages & Config Keys** | `grep` / `ripgrep` | Works identically across `.json`, `.yaml`, `.env`, and non-code assets. |
| **Enum Values & Data Constants** | `grep` / `ripgrep` | Simple primitives have no dispatch graph; text search locates exact tokens. |
| **Control Flow & Guard Clauses** | Direct File Read | Graphs reveal *who calls whom*; reading code reveals *under what conditions*. |

---

## 🎯 Deterministic Exit Codes

`csmesh` uses strict, deterministic exit codes so automated agents can branch reliably without fuzzy text parsing:

| Code | Status | Meaning | Recommended Agent Action |
|:---:|:---|:---|:---|
| `0` | **Success** | Complete answer returned within budget. | Parse output directly. |
| `1` | **Not Found** | Symbol does not exist in repository. | Check spelling or verify namespace. |
| `2` | **Over Budget** | Answer exists but exceeds `--budget`. | Re-run with narrower `--depth` or query a specific callee. |
| `3` | **Ambiguous** | Multiple symbols match query, including the same name declared in more than one project. | Re-run with qualified `Type.Member`, or with `--project <path>` taken from the candidate list. |
| `4` | **No Index** | No usable graph: not generated, or (for `review`) the index predates HEAD. | Execute `csmesh index` and retry. |
| `5` | **Changed** (`review` only) | Unaccepted structural change vs. the base revision. | Review the finding, then `csmesh review --accept` if it's fine to keep. |
| `64`| **Usage Error** | Invalid flags, syntax, or arguments, including `review --accept` while the index predates HEAD. | Run `csmesh <cmd> --help`. |
| `70`| **Internal Error** | Unhandled failure inside csmesh. | Re-run with `--debug` and open an issue. |
| `75`| **Contended** | The graph file is held by another process; the write did not happen. | Nothing is broken — wait briefly and re-run the command. |

---

## 📊 Telemetry & Audit Logging

Every invocation records an audit log entry in `.csmesh/usage.jsonl` (local to the repository, never sent to external servers):

```json
{"ts":"2026-09-03T15:15:42Z","caller":"claude-code","caller_via":"env:CLAUDECODE","tty":false,"cmd":"trace","args":"PaymentController.Post --budget 600","exit":0,"ms":84,"budget":600,"out_tokens":125,"nodes":160,"edges":380}
```

Caller detection automatically attributes queries based on environment variables and process trees (`claude-code`, `cursor`, `windsurf`, `cline`, `antigravity`, `terminal-human`).

> To disable telemetry entirely, pass `--no-telemetry` or set `CSMESH_NO_TELEMETRY=1`.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
