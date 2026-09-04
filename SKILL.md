---
name: csmesh
description: >
  Use this skill in ANY repository that contains C# or .NET code -- if you see a .sln or .slnx, a .csproj,
  or .cs files, this skill applies and you should reach for csmesh before grep, ripgrep, glob or
  reading files to answer structural questions. Structural questions are: what does this method
  end up calling, which class implements this interface, what breaks if I change this member,
  where does this HTTP route go, which handler runs for this command or message. csmesh resolves
  the indirection that text search cannot see: dependency injection registrations, MediatR
  Send/Publish, interface dispatch, attribute routing, MassTransit consumers.
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

0. First time in a repository, run `csmesh map`. It tells you which projects depend on which,
   where the HTTP and message entrypoints cluster, and the few members everything runs through.
   Do not use `ls` or `tree` for orientation -- folder names do not say what is load bearing.
1. Before opening a **second** file to follow a call path, run `csmesh trace`.
2. Before changing any `public` member, run `csmesh blast-radius`.
3. When you meet an interface and need to know what actually runs, run `csmesh impl` --
   do not guess from naming convention, and do not assume there is only one implementation.
4. When you see `_mediator.Send(...)`, `Publish(...)`, or a message bus call, run `csmesh trace`
   on the calling method. grep will not find the handler; csmesh links it.
5. To find where an HTTP path or a background job is served, run `csmesh entrypoints <filter>`.
6. After you edit anything, run `csmesh diff` before you claim the change is safe. It takes the
   git diff you already made and tells you what those symbols reach -- entrypoints, production
   callers, and tests -- which is the question you actually have, not "what if I changed X".
7. When a query returns less than you expected, run `csmesh unresolved` before falling back to
   grep. It says whether the edge is missing or the code is absent. Those are not the same
   answer and only this command separates them.
8. When you need more than one of the above about the *same* symbol, run `csmesh context` once
   instead of chaining three commands.
10. After a refactor, run `csmesh changes`. `diff` says what you edited; this says whether a
    DI binding or a mediator dispatch stopped resolving. The compiler catches neither, and unit
    tests that inject mocks do not either.
9. When you have two symbols and want to know how one reaches the other -- a class in a stack
   trace, an endpoint and a repository -- run `csmesh path <from> <to>`. Neither `trace` nor
   `blast-radius` can answer that; they each walk from one end only.

11. Symbol lookups cover enums, enum members, delegates and fields as well as types and methods.
    `csmesh blast-radius OrderStatus` answers "if I add a member, which switches must I revisit";
    do not grep for the enum name to find that out.
12. If a lookup reports a symbol comes from a referenced assembly, stop looking for it here. It is
    a package type, not a missing file, and no amount of grepping this repository will find it.

13. When any command exits 1, do not fall back to grep. Run `csmesh silence <symbol>` (or
    `csmesh silence <from> <to>`) first. Exit 1 means the graph had nothing, which is not the same
    as the codebase having nothing, and this says which one it was: a typo, a package type, an
    unbuilt solution, or a container scan. Only one of those is fixed by searching this repository.

Keep using grep for what it is good at: string literals, config values, TODOs, error messages.

## Commands

```bash
csmesh map                                     # orient first in an unfamiliar repo
csmesh index                                   # once per session if doctor says the index is stale
csmesh trace PaymentController.Post --budget 600
csmesh impl IPaymentGateway --budget 300
csmesh blast-radius Order.Status --budget 800 --depth 2
csmesh entrypoints payments
csmesh context PaymentService.Process --budget 800
csmesh path PaymentController.Post StripeGateway.Authorize
csmesh cycles --namespace --budget 400
csmesh changes                                 # did a binding or dispatch disappear?
csmesh cycles --project
csmesh diff --budget 800                       # after editing: what did I just affect?
csmesh unresolved --kind di                    # why is an answer thinner than expected?
csmesh silence IPaymentGateway                 # exit 1: absent, or just unseen?
csmesh doctor
```

## Reading the output

Each row is `Symbol  [edge marker]  {tags}  file:line`.

- `[impl, di-bound]` -- this is the implementation registered in DI, so it is the one that runs.
- `[mediatr via Send(CreatePaymentCommand)]` -- the call is dispatched, not direct.
- `{http:POST /payments}` -- this member is an HTTP entrypoint.
- `[STALE]` -- the file changed after the index was built. **Do not trust this row.** Re-run
  `csmesh index`, or open that one file directly to confirm.
- `@ Api/Registrations.cs:22` -- where the edge was wired up, next to where the target is
  defined. Go there to change a binding; do not grep for it.
- `[?0.75 assembly-scan]` -- bound by Scrutor/MediatR-style assembly scanning. The family
  is wired, but the exact pair was not named in source. Confirm before relying on it.
- `MEMBERS` in `context` on a type or enum -- the shape, with nullable annotations
  (`HostId  Guid?`). This is the answer to "what does this hold"; do not open the file for it.
- `{test}` -- test code. Still a real caller, but not what breaks in production;
  `blast-radius` and `diff` list it separately for that reason.
- `[... ?0.70 short-name-match]` -- the edge came from a name match, not a compiler symbol.
  Anything below `0.80` is a lead, not a fact: open the file before you act on it. Rows without a
  `?score` were read straight off a symbol and are exact.

## Exit codes -- branch on these, do not parse the text

| code | meaning | what to do |
|---|---|---|
| 0 | complete answer | use it |
| 1 | symbol not found | check spelling, or the symbol is not in this repo |
| 2 | answer exists but exceeds the budget | re-run with `--depth 2`, or trace a narrower symbol. Do **not** just raise `--budget` to a huge number |
| 3 | ambiguous | re-run with `Type.Member`, not a bare member name |
| 4 | no index | run `csmesh index` |

## Rules

- On a large solution, narrow with `--under src/Api` before raising `--budget`. Scoping the
  question is cheaper than paying for the whole tree.
- Always pass `--budget`. Default it to 600 for `trace`, 300 for `impl`, 800 for `blast-radius`.
- Prefer `Type.Member` over a bare member name; bare names cost an extra round trip via exit 3.
- Chain calls in one shell command when you have two questions:
  `csmesh impl IPaymentGateway --budget 200 && csmesh blast-radius Order.Status --budget 400`
- `csmesh doctor` reports how much of the graph resolved. If call resolution is low, the answers
  are thin because edges are missing, not because the code is not there -- run `dotnet build`
  first, then re-index.
- csmesh tells you which files matter. Open those files. It is not a replacement for reading the
  code you are about to change -- it is a replacement for hunting for it.