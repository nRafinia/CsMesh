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

1. Before opening a **second** file to follow a call path, run `csmesh trace`.
2. Before changing any `public` member, run `csmesh blast-radius`.
3. When you meet an interface and need to know what actually runs, run `csmesh impl` --
   do not guess from naming convention, and do not assume there is only one implementation.
4. When you see `_mediator.Send(...)`, `Publish(...)`, or a message bus call, run `csmesh trace`
   on the calling method. grep will not find the handler; csmesh links it.
5. To find where an HTTP path or a background job is served, run `csmesh entrypoints <filter>`.
6. When you need more than one of the above about the *same* symbol, run `csmesh context` once
   instead of chaining three commands.
7. When you have two symbols and want to know how one reaches the other -- a class in a stack
   trace, an endpoint and a repository -- run `csmesh path <from> <to>`. Neither `trace` nor
   `blast-radius` can answer that; they each walk from one end only.

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
- `[mediatr via Send(CreatePaymentCommand)]` -- the call is dispatched, not direct.
- `{http:POST /payments}` -- this member is an HTTP entrypoint.
- `[STALE]` -- the file changed after the index was built. **Do not trust this row.** Re-run
  `csmesh index`, or open that one file directly to confirm.
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

- Always pass `--budget`. Default it to 600 for `trace`, 300 for `impl`, 800 for `blast-radius`.
- Prefer `Type.Member` over a bare member name; bare names cost an extra round trip via exit 3.
- Chain calls in one shell command when you have two questions:
  `csmesh impl IPaymentGateway --budget 200 && csmesh blast-radius Order.Status --budget 400`
- `csmesh doctor` reports how much of the graph resolved. If call resolution is low, the answers
  are thin because edges are missing, not because the code is not there -- run `dotnet build`
  first, then re-index.
- csmesh tells you which files matter. Open those files. It is not a replacement for reading the
  code you are about to change -- it is a replacement for hunting for it.