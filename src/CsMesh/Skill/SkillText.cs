namespace CsMesh.Skill;

/// <summary>
/// The text written by <c>csmesh skill --install</c>.
///
/// <see cref="Markdown"/> is byte-identical to SKILL.md at the repository root; the two drifted
/// apart before, and an installed rule set that disagrees with the documented one is worse than
/// either alone. <see cref="Rules"/> is the compact form for assistants that take a single rules
/// file rather than a skill folder, and <see cref="CursorMdc"/> is the same body behind Cursor's
/// front matter.
/// </summary>
public static class SkillText
{
    public const string Markdown =
        """
        ---
        name: csmesh
        description: >
          Use this skill in ANY repository that contains C# or .NET code -- if you see a .sln or .slnx, a
          .csproj, or .cs files, this skill applies. Reach for csmesh before grep, ripgrep, glob, reading
          files, or delegating discovery to a subagent. It answers structural questions from a prebuilt
          symbol graph in one shell call: what does this end up calling, which class actually runs behind
          this interface, what breaks if I change this, where does this route go, which handler receives
          this command, what did my last edit affect. It resolves the indirection text search cannot see --
          dependency injection registrations including assembly scanning, MediatR Send/Publish, interface
          dispatch, attribute routing, minimal API endpoints, MassTransit consumers.
        ---

        # csmesh

        One shell call against a prebuilt symbol graph, in place of the read-file / grep / read-file loop
        that spends a turn per hop.

        ## When this applies

        If the repository contains `.cs`, `.csproj`, `.sln` or `.slnx` files, this skill is in scope. Check
        once at the start of the task. If it is a .NET repo, use csmesh for the questions below for the
        rest of the session.

        ## Match what you are about to do

        These are keyed on the thing you are about to reach for, not on a condition to check first.

        **"Find me everything about X." "Explore how X works." "Where is X and what touches it?"**
        -> `csmesh context X`

        This is the one that gets skipped. The reflex is to spawn a discovery subagent or start a broad
        search, and it feels natural because the question is broad. It is the same question, answered from
        a graph in one call instead of a sub-session of file reads. Use a subagent for what csmesh cannot
        know: intent, naming, business rules, why a decision was made. Not for where things are and what
        connects to what.

        **"I am new to this repository. Where do I start?"**
        -> `csmesh map`

        Which projects depend on which, where the entrypoints cluster, the few members everything runs
        through. Not `ls`, not `tree`, not a directory listing -- a folder name does not say whether
        anything inside it is load bearing.

        **"What does this method actually do / end up calling?"**
        -> `csmesh trace Type.Member`

        Run it before you open a **second** file to follow a call chain. It crosses container bindings and
        mediator dispatch, which reading files in sequence does not.

        **"Which class runs behind this interface?"**
        -> `csmesh impl IThing`

        Never guess from a naming convention, and never assume there is only one. The output ranks the
        registered implementation first, labels test doubles, and prints where the binding was declared so
        you do not have to look for it.

        **"What breaks if I change this?"**
        -> `csmesh blast-radius Type.Member`

        Before changing any `public` member. Production callers and test callers are listed separately,
        with how many projects the change reaches.

        **"I see `_mediator.Send(...)` or a bus publish. Where does it go?"**
        -> `csmesh trace` on the calling method

        grep cannot find the handler; the request type is matched to it here.

        **"Where is this HTTP route served? What background jobs exist?"**
        -> `csmesh entrypoints <filter>`

        **"How does A end up reaching B?"**
        -> `csmesh path <from> <to>`

        A class in a stack trace and the endpoint above it; an endpoint and the repository under it.
        `trace` walks forward from one end and `blast-radius` backward from one end -- neither connects
        two named symbols.

        **"I just edited things. Is the change safe?"**
        -> `csmesh diff`

        Takes the git diff you already made and reports what those symbols reach. This is the real
        question, not the hypothetical "what if I changed X".

        **"I finished a refactor."**
        -> `csmesh changes`

        `diff` says what you edited. This says whether a DI binding or a mediator dispatch stopped
        resolving. The compiler catches neither, and unit tests that inject mocks do not either.

        **"What does this type hold? Is this field nullable?"**
        -> `csmesh context TypeName`, read the `MEMBERS` section

        Names, types and nullable annotations (`HostId  Guid?`). Do not open the file for the shape.

        **"If I add a member to this enum, what has to change?"**
        -> `csmesh blast-radius EnumName`

        Enums, enum members, delegates and fields are all in the graph. Do not grep for the enum name.

        ## When something comes back empty

        **A command exits 1.** Do not fall back to grep. Run `csmesh silence <symbol>`, or
        `csmesh silence <from> <to>` for a missing path. Exit 1 means the graph had nothing, which is not
        the same as the codebase having nothing. It tells you which: a typo, a type from a referenced
        package, a solution that was not built, or a container scan the indexer cannot follow. Only one of
        those is fixed by searching this repository.

        **An answer is thinner than you expected.** Run `csmesh unresolved`. It reports where the indexer
        failed and why, grouped by reason. A missing edge and an absent symbol look identical everywhere
        else.

        **A lookup says a symbol comes from a referenced assembly.** Stop looking for it here. It is a
        package type, not a missing file.

        **`csmesh doctor` reports low call resolution.** The answers are thin because edges are missing,
        not because the code is absent. Fix that before trusting anything the graph says.

        ## Keep using grep for

        String literals, config values, TODOs, error messages, log text, anything in a `.json`, `.yml` or
        `.razor` file. csmesh knows symbols, not text.

        ## Commands

        ```bash
        csmesh map                                          # orient first in an unfamiliar repo
        csmesh context PaymentService.Process --budget 800  # everything about one symbol, one call
        csmesh trace PaymentController.Post --budget 600
        csmesh impl IPaymentGateway --budget 300
        csmesh blast-radius Order.Status --budget 800 --depth 2
        csmesh path PaymentController.Post StripeGateway.Authorize
        csmesh entrypoints payments
        csmesh diff --budget 800                            # after editing: what did I just affect?
        csmesh changes                                      # after a refactor: did a binding vanish?
        csmesh silence IPaymentGateway                      # exit 1: absent, or just unseen?
        csmesh unresolved --kind di                         # why is an answer thinner than expected?
        csmesh cycles --project
        csmesh index                                        # once per session if doctor says it is stale
        csmesh doctor
        ```

        ## Reading the output

        Each row is `Symbol  [edge marker]  {tags}  file:line`.

        - `[impl, di-bound]` -- registered in the container, so this is the one that runs.
        - `[mediatr via Send(CreatePaymentCommand)]` -- dispatched, not called directly.
        - `@ Api/Registrations.cs:22` -- where the edge was wired up, beside where the target is defined.
          Go there to change a binding; do not search for it.
        - `{http:POST /payments}` -- an HTTP entrypoint.
        - `{test}` -- test code. A real caller, but not what breaks in production, which is why
          `blast-radius` and `diff` list it apart.
        - `[... ?0.70 short-name-match]` or `[?0.75 assembly-scan]` -- confidence. The edge came from a
          name match or from container scanning, not from a compiler symbol. **Below `0.80` is a lead, not
          a fact**: open the file before acting on it. A row with no `?score` was read straight off a
          symbol and is exact.
        - `[STALE]` -- the file changed after the index was built. **Do not trust this row.** Add `--heal`
          and run the same command again: the changed files are rebound in place first. `csmesh index` on
          its own is incremental too, and only rebinds what moved.

        ## Exit codes -- branch on these, do not parse the text

        | code | meaning | what to do |
        |---|---|---|
        | 0 | complete answer | use it |
        | 1 | nothing found | `csmesh silence <symbol>` before anything else |
        | 2 | answer exists but exceeds the budget | narrow with `--under`, or use the depth the message names. Do **not** just raise `--budget` to a huge number |
        | 3 | ambiguous | re-run with `Type.Member`, not a bare member name |
        | 4 | no index, or one written by an older csmesh | run `csmesh index` |
        | 64 | bad command line | run `csmesh <cmd> --help` |
        | 70 | csmesh itself failed | re-run with `--debug`; that is a bug, not your query |

        ## Rules

        - Always pass `--budget`. Default it to 600 for `trace`, 300 for `impl`, 800 for `blast-radius`,
          `context` and `diff`, 400 for `path`.
        - On a large solution, narrow with `--under src/Api` before raising `--budget`. Scoping the
          question is cheaper than paying for the whole tree.
        - Prefer `Type.Member` over a bare member name; a bare name costs a round trip via exit 3.
        - On overflow, `trace` names a depth that fits and prints the command to re-run. Use that rather
          than guessing a smaller number.
        - Chain two questions into one shell call:
          `csmesh impl IPaymentGateway --budget 200 && csmesh blast-radius Order.Status --budget 400`
        - csmesh tells you which files matter. Open those files. It replaces hunting for code, not reading
          the code you are about to change.
        """;

    public const string Rules =
        """
        # csmesh: C# structural code intelligence

        In any repository with `.cs`, `.csproj`, `.sln` or `.slnx` files, reach for `csmesh` before grep,
        ripgrep, glob, reading files in sequence, or handing discovery to a subagent. It answers from a
        prebuilt symbol graph in one shell call and resolves what text search cannot see: DI registrations
        including assembly scanning, MediatR Send/Publish, interface dispatch, attribute routing.

        ## Match the thing you are about to do

        | you are about to | run instead |
        |---|---|
        | spawn a subagent to "find everything about X" or explore how X works | `csmesh context X --budget 800` |
        | list directories to orient in an unfamiliar repo | `csmesh map` |
        | open a second file to follow a call chain | `csmesh trace Type.Member --budget 600` |
        | guess which class implements an interface | `csmesh impl IThing --budget 300` |
        | change a `public` member | `csmesh blast-radius Type.Member --budget 800` |
        | grep for a mediator handler | `csmesh trace` on the calling method |
        | search for an HTTP route or a background job | `csmesh entrypoints <filter>` |
        | work out how A reaches B | `csmesh path <from> <to>` |
        | claim an edit is safe | `csmesh diff --budget 800` |
        | finish a refactor | `csmesh changes` |
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

        - **Exit 1**: do not fall back to grep. Run `csmesh silence <symbol>` (or `<from> <to>`). It says
          whether the symbol was mistyped, lives in a package, was never bound because the solution was not
          built, or is reached only through a container scan. Only one of those is fixed by searching here.
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

        `0` ok, `1` nothing found, `2` over budget, `3` ambiguous, `4` no index, `64` bad command line,
        `70` internal error. Branch on these; do not parse the text.

        ## Practice

        - Always pass `--budget`: 600 `trace`, 300 `impl`, 800 `blast-radius`/`context`/`diff`, 400 `path`.
        - Narrow with `--under src/Api` before raising `--budget`.
        - Prefer `Type.Member` over a bare name; a bare name costs a round trip via exit 3.
        - On overflow, `trace` names a depth that fits and prints the command to re-run. Use it.
        - csmesh tells you which files matter. Open those files. It replaces hunting for code, not reading
          the code you are about to change.
        """;

    public const string CursorMdc =
        """
        ---
        description: C# structural code navigation, blast radius and change analysis with csmesh
        globs: ["**/*.cs", "**/*.csproj", "**/*.sln", "**/*.slnx"]
        alwaysApply: false
        ---

        # csmesh: C# structural code intelligence

        In any repository with `.cs`, `.csproj`, `.sln` or `.slnx` files, reach for `csmesh` before grep,
        ripgrep, glob, reading files in sequence, or handing discovery to a subagent. It answers from a
        prebuilt symbol graph in one shell call and resolves what text search cannot see: DI registrations
        including assembly scanning, MediatR Send/Publish, interface dispatch, attribute routing.

        ## Match the thing you are about to do

        | you are about to | run instead |
        |---|---|
        | spawn a subagent to "find everything about X" or explore how X works | `csmesh context X --budget 800` |
        | list directories to orient in an unfamiliar repo | `csmesh map` |
        | open a second file to follow a call chain | `csmesh trace Type.Member --budget 600` |
        | guess which class implements an interface | `csmesh impl IThing --budget 300` |
        | change a `public` member | `csmesh blast-radius Type.Member --budget 800` |
        | grep for a mediator handler | `csmesh trace` on the calling method |
        | search for an HTTP route or a background job | `csmesh entrypoints <filter>` |
        | work out how A reaches B | `csmesh path <from> <to>` |
        | claim an edit is safe | `csmesh diff --budget 800` |
        | finish a refactor | `csmesh changes` |
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

        - **Exit 1**: do not fall back to grep. Run `csmesh silence <symbol>` (or `<from> <to>`). It says
          whether the symbol was mistyped, lives in a package, was never bound because the solution was not
          built, or is reached only through a container scan. Only one of those is fixed by searching here.
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

        `0` ok, `1` nothing found, `2` over budget, `3` ambiguous, `4` no index, `64` bad command line,
        `70` internal error. Branch on these; do not parse the text.

        ## Practice

        - Always pass `--budget`: 600 `trace`, 300 `impl`, 800 `blast-radius`/`context`/`diff`, 400 `path`.
        - Narrow with `--under src/Api` before raising `--budget`.
        - Prefer `Type.Member` over a bare name; a bare name costs a round trip via exit 3.
        - On overflow, `trace` names a depth that fits and prints the command to re-run. Use it.
        - csmesh tells you which files matter. Open those files. It replaces hunting for code, not reading
          the code you are about to change.
        """;
}