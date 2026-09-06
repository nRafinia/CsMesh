namespace CsMesh.Common;

/// <summary>
/// The grep hook, embedded so that installing it needs nothing beside the binary.
///
/// Two versions rather than one, because the hook runs as whatever the platform can execute:
/// a bash script is not runnable on a stock Windows box, and shipping only that would mean the
/// install quietly produced a hook that never fired.
/// </summary>
public static class HookText
{
    public const string Bash =
        @"#!/usr/bin/env bash
# Nudges an agent from grep towards csmesh when it is searching C# source.
#
# Advisory only. It never blocks: grep over .cs files is the right tool for log messages,
# string literals, TODOs and anything the symbol graph does not hold, and a hook that
# refused those would be wrong more often than it was right.
#
# The reply must be a single line of JSON on stdout. Claude Code reads PreToolUse output as
# a structured payload and passes hookSpecificOutput.additionalContext to the model; plain
# text is discarded without an error, so an earlier version of this script that printed a
# readable hint accomplished exactly nothing while appearing to work.

set -euo pipefail

payload=""$(cat)""

# No jq on most machines, so this stays a grep over the raw JSON rather than a parse.
targets_csharp() {
  printf '%s' ""$payload"" | grep -qiE '\.cs(""|\\""|\b)|--include=\*?\.cs|""type""[[:space:]]*:[[:space:]]*""cs""'
}

pattern=""$(printf '%s' ""$payload"" \
  | sed -n 's/.*""pattern""[[:space:]]*:[[:space:]]*""\([^""]*\)"".*/\1/p' | head -1)""

targets_csharp || exit 0

# A search for prose or a literal is exactly what grep is for. Only identifier-shaped
# patterns are worth redirecting.
case ""$pattern"" in
  *"" ""*|"""") exit 0 ;;
esac

# Escape anything that would break the JSON string. Newlines become \n rather than real
# breaks, because the transport is one line.
escape() {
  printf '%s' ""$1"" | sed -e 's/\\/\\\\/g' -e 's/""/\\""/g' | awk '{ printf ""%s\\n"", $0 }'
}

hint=""csmesh is indexed for this repository and resolves what grep cannot: DI bindings,
mediator dispatch, interface-to-implementation routing and attribute routing.

For '${pattern}', try first:
  csmesh where ${pattern}            -- where it lives, ranked by what reaches it
  csmesh impl ${pattern}             -- implementations and their DI registration
  csmesh trace ${pattern}            -- call tree through interfaces and dispatch
  csmesh blast-radius ${pattern}     -- what breaks if it changes

Stay with grep for string literals, config keys, log messages and non-.cs files.""

printf '{""hookSpecificOutput"":{""hookEventName"":""PreToolUse"",""additionalContext"":""%s""}}\n' ""$(escape ""$hint"")""

exit 0
";

    public const string PowerShell =
        @"# Nudges an agent from grep towards csmesh when it is searching C# source.
# Advisory only: it never blocks. grep over .cs is the right tool for string literals,
# log messages and TODOs, and a hook that refused those would be wrong more often than right.
#
# Output must be a single line of JSON. Claude Code reads PreToolUse output as a structured
# payload and passes hookSpecificOutput.additionalContext to the model; plain text is
# discarded without an error, so a readable hint accomplishes nothing while appearing to work.

$ErrorActionPreference = ""Stop""
$raw = [Console]::In.ReadToEnd()

if ($raw -notmatch '\.cs') { exit 0 }

$pattern = """"
if ($raw -match '""pattern""\s*:\s*""([^""]*)""') { $pattern = $Matches[1] }

# Prose and literals are exactly what grep is for. Only identifier-shaped patterns are
# worth redirecting.
if ([string]::IsNullOrWhiteSpace($pattern) -or $pattern.Contains("" "")) { exit 0 }

$hint = @""
csmesh is indexed for this repository and resolves what grep cannot: DI bindings,
mediator dispatch, interface-to-implementation routing and attribute routing.

For '$pattern', try first:
  csmesh where $pattern            -- where it lives, ranked by what reaches it
  csmesh impl $pattern             -- implementations and their DI registration
  csmesh trace $pattern            -- call tree through interfaces and dispatch
  csmesh blast-radius $pattern     -- what breaks if it changes

Stay with grep for string literals, config keys, log messages and non-.cs files.
""@

# ConvertTo-Json handles the escaping, which hand-rolled quoting gets wrong on the first
# pattern containing a backslash.
$payload = @{
  hookSpecificOutput = @{
    hookEventName    = ""PreToolUse""
    additionalContext = $hint
  }
}

$payload | ConvertTo-Json -Depth 5 -Compress
exit 0
";
}
