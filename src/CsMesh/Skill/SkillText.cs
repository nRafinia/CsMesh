namespace CsMesh.Skill;

public static class SkillText
{
    public const string Markdown =
        """
        ---
        name: csmesh
        description: >
          Use this skill in ANY repository that contains C# or .NET code -- if you see a .sln or .slnx, a .csproj,
          or .cs files, this skill applies and you should reach for csmesh before grep, ripgrep, glob or
          reading files to answer structural questions. Structural questions are: what does this method
          end up calling, which class implements this interface, what breaks if I change this member,
          where does this HTTP route go, which handler runs for this command or message. csmesh resolves
          the indirection that text search cannot see: dependency injection registrations, MediatR
          Send/Publish, interface dispatch, attribute routing, minimal API endpoints, MassTransit consumers.
        ---

        # csmesh

        A local command that answers structural questions about a C# codebase from a pre-built symbol
        graph. One call replaces the read-file / grep / read-file loop that burns a turn per hop.

        ## When this applies

        If the repository contains `.cs`, `.csproj`, `.sln` or `.slnx` files, this skill is in scope. Check once at
        the start of the task; if it is a .NET repo, use csmesh for the questions below for the rest of
        the session.

        ## Decision rules

        Follow these instead of your default search behaviour.

        1. Before opening a **second** file to follow a call path, run `csmesh trace`.
        2. Before changing any `public` member, run `csmesh blast-radius`.
        3. When you meet an interface or abstract base and need to know what actually runs, run
           `csmesh impl` -- do not guess from naming convention, and do not assume there is only
           one implementation.
        4. When you see `_mediator.Send(...)`, `Publish(...)`, or a message bus call, run `csmesh trace`
           on the calling method. grep will not find the handler; csmesh links it.
        5. To find where an HTTP path or a background job is served, run `csmesh entrypoints <filter>`.
        6. When you need more than one of the above about the *same* symbol, run `csmesh context`
           once rather than chaining three commands.
        7. When you have two symbols and want to know how one reaches the other -- a class you found
           in a stack trace, an endpoint and the repository underneath it -- run
           `csmesh path <from> <to>`. Neither `trace` nor `blast-radius` answers that; each walks
           from one end only.

        Keep using grep for what it is good at: string literals, config values, TODOs, error messages.

        ## Commands

        ```bash
        csmesh index                                   # once per session if doctor says the index is stale
        csmesh trace PaymentController.Post --budget 600
        csmesh impl IPaymentGateway --budget 300
        csmesh blast-radius Order.Status --budget 800 --depth 2
        csmesh entrypoints payments
        csmesh context PaymentService.Process --budget 800
        csmesh path PaymentController.Post StripeGateway.Authorize
        csmesh cycles --namespace --budget 400
        csmesh doctor
        ```

        ## Reading the output

        Each row is `Symbol  [edge marker]  {tags}  file:line`.

        - `[impl, di-bound]` -- this is the implementation registered in DI, so it is the one that runs.
        - `[override]` -- a base class member overridden by a derived type.
        - `[mediatr via Send(CreatePaymentCommand)]` -- the call is dispatched, not direct.
        - `[route GET /orders]` -- a minimal API endpoint registration.
        - `{http:POST /payments}` -- this member is an HTTP entrypoint.
        - `[STALE]` -- the file changed after the index was built. **Do not trust this row.** Re-run
          `csmesh index`, or open that one file directly to confirm.
        - `[... ?0.70 short-name-match]` -- confidence. The edge came from a name match, not a
          compiler symbol. Below `0.80`, treat the row as a lead and open the file before acting on
          it. A row with no `?score` was read straight off a symbol and is exact.

        Pass `--json` when you want to branch on structure instead of parsing the text layout.

        ## Exit codes -- branch on these, do not parse the text

        | code | meaning | what to do |
        |---|---|---|
        | 0 | complete answer | use it |
        | 1 | symbol not found | check spelling, or the symbol is not in this repo |
        | 2 | answer exists but exceeds the budget | re-run with `--depth 2`, or trace a narrower symbol. Do **not** just raise `--budget` to a huge number |
        | 3 | ambiguous | re-run with `Type.Member`, not a bare member name |
        | 4 | no index, or an index written by an older csmesh | run `csmesh index` |
        | 64 | bad command line | run `csmesh <cmd> --help` |
        | 70 | csmesh itself failed | re-run with `--debug`; this is a bug, not your query |

        ## Rules

        - Always pass `--budget`. Default it to 600 for `trace`, 300 for `impl`, 800 for
          `blast-radius` and `context`, 400 for `path`.
        - Prefer `Type.Member` over a bare member name; bare names cost an extra round trip via exit 3.
        - Chain calls in one shell command when you have two questions:
          `csmesh impl IPaymentGateway --budget 200 && csmesh blast-radius Order.Status --budget 400`
        - `csmesh doctor` reports how much of the graph resolved. Low call resolution means edges
          are missing, not that the code is absent -- run `dotnet build`, then re-index.
        - csmesh tells you which files matter. Open those files. It is not a replacement for reading the
          code you are about to change -- it is a replacement for hunting for it.
        """;

    public const string Rules =
        """
        # csmesh: C# Codebase Symbol Graph

        Whenever working with C# / .NET code in this repository (`.cs`, `.csproj`, `.sln`, `.slnx`):

        ## Primary Rules
        Always prefer `csmesh` CLI over `grep`, `ripgrep`, glob, or repeated file reads for structural code navigation:

        1. **Before opening a second file to trace execution**: run `csmesh trace <Type.Member> --budget 600`.
        2. **Before modifying any public member**: run `csmesh blast-radius <Type.Member> --budget 800`.
        3. **To find implementations of an interface or base class & DI binding**: run `csmesh impl <IInterface> --budget 300`.
        4. **To locate endpoints, handlers, or hosted services**: run `csmesh entrypoints [filter]`.
        5. **If output contains `[STALE]` or after making significant code modifications**: run `csmesh index` to refresh the graph.

        ## Exit codes
        `0` ok, `1` not found, `2` over budget, `3` ambiguous, `4` no index, `64` bad command line, `70` internal error.

        ## Best Practices
        - Always supply `--budget` (defaults: `trace` 600, `impl` 300, `blast-radius` 800).
        - Prefer qualified names (`Type.Member`) over bare member names to avoid ambiguous matches.
        - Use `--json` when branching on the result programmatically.
        - Keep using `grep` only for string literals, error messages, config keys, and non-C# files.
        """;

    public const string CursorMdc =
        """
        ---
        description: C# structural code navigation and blast radius analysis with csmesh
        globs: ["**/*.cs", "**/*.csproj", "**/*.sln", "**/*.slnx"]
        alwaysApply: false
        ---

        # csmesh: C# Codebase Symbol Graph

        Whenever working with C# / .NET code in this repository (`.cs`, `.csproj`, `.sln`, `.slnx`):

        ## Primary Rules
        Always prefer `csmesh` CLI over `grep`, `ripgrep`, glob, or repeated file reads for structural code navigation:

        1. **Before opening a second file to trace execution**: run `csmesh trace <Type.Member> --budget 600`.
        2. **Before modifying any public member**: run `csmesh blast-radius <Type.Member> --budget 800`.
        3. **To find implementations of an interface or base class & DI binding**: run `csmesh impl <IInterface> --budget 300`.
        4. **To locate endpoints, handlers, or hosted services**: run `csmesh entrypoints [filter]`.
        5. **If output contains `[STALE]` or after making significant code modifications**: run `csmesh index` to refresh the graph.

        ## Exit codes
        `0` ok, `1` not found, `2` over budget, `3` ambiguous, `4` no index, `64` bad command line, `70` internal error.

        ## Best Practices
        - Always supply `--budget` (defaults: `trace` 600, `impl` 300, `blast-radius` 800).
        - Prefer qualified names (`Type.Member`) over bare member names to avoid ambiguous matches.
        - Use `--json` when branching on the result programmatically.
        - Keep using `grep` only for string literals, error messages, config keys, and non-C# files.
        """;
}
