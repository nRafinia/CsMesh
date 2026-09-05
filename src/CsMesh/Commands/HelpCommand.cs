using CsMesh.Common;

namespace CsMesh.Commands;

public static class HelpCommand
{
    public static int Show(string? command = null)
    {
        var normalized = command?.ToLowerInvariant().Trim();

        Console.WriteLine(normalized switch
        {
            "index" => IndexHelp,
            "trace" => TraceHelp,
            "impl" => ImplHelp,
            "blast-radius" or "blast" => BlastRadiusHelp,
            "entrypoints" => EntrypointsHelp,
            "context" => ContextHelp,
            "path" or "why" => PathHelp,
            "cycles" => CyclesHelp,
            "unresolved" => UnresolvedHelp,
            "diff" => DiffHelp,
            "changes" => ChangesHelp,
            "silence" or "why-not" => SilenceHelp,
            "map" => MapHelp,
            "where" or "find" => WhereHelp,
            "usage" => UsageHelp,
            "doctor" => DoctorHelp,
            "skill" => SkillHelp,
            "version" => VersionHelp,
            _ => MainHelp
        });

        return Exit.Ok;
    }

    public const string MainHelp =
        """
        CsMesh - Structural code intelligence for C# and .NET under a token budget.

        USAGE:
            csmesh <COMMAND> [OPTIONS]

        COMMANDS:
            map            Where the weight is: projects, entrypoints, hotspots
            where          Find the symbols a word belongs to, ranked by what reaches them
            index          Build or refresh the symbol graph for the repository
            trace          Trace execution paths through DI, MediatR, and interfaces
            impl           Find implementations of an interface or base class, DI-bound first
            blast-radius   Discover callers and entrypoints affected by changing a symbol
            entrypoints    Find HTTP endpoints, message handlers, consumers, and jobs
            context        Everything structural about one symbol, in a single call
            path           Shortest route from one symbol to another (alias: why)
            cycles         Circular dependencies between types or namespaces
            unresolved     Where the indexer failed, grouped by reason
            diff           Symbols a git diff touched, and what they reach
            changes        Bindings, dispatches and implementations that appeared or vanished
            silence        Why a query came back empty (alias: why-not)
            usage          Display local invocation metrics and caller attribution
            doctor         Diagnose index freshness, skill installation, and environment
            skill          Display skill markdown or install agent skill/rule files
            version        Print version information
            help           Print this message or the help of the given subcommand(s)

        GLOBAL OPTIONS:
            --repo <PATH>      Repository root (default: nearest .sln/.slnx/.git above cwd)
            --under <PATH>     Restrict to a subtree, e.g. --under src/Api
            --budget <N>       Maximum output tokens (trace 600, impl 300, blast-radius 800)
            --depth <N>        Traversal depth limit (trace 6, blast-radius 3, context 3, path 12)
            --json             Output results as a structured JSON envelope
            --debug            Enable verbose diagnostics on stderr
            --heal             Rebind changed files before answering instead of marking rows [STALE]
            --no-telemetry     Do not record this invocation in usage telemetry
            -h, --help         Print help information

        EXIT CODES:
            0 ok   1 not-found   2 over-budget   3 ambiguous   4 no-index
            64 usage-error   70 internal-error

        CONFIDENCE:
            A row marked [... ?0.70 short-name-match] came from a name match, not a compiler
            symbol. Treat anything below 0.80 as a lead to verify, not a fact. 'csmesh doctor'
            reports how many such edges the graph holds.

        Run 'csmesh help <COMMAND>' or 'csmesh <COMMAND> --help' for command-specific options.
        """;

    public const string SkillHelp =
        """
        csmesh skill - Display skill markdown or install agent skill and rule files

        USAGE:
            csmesh skill [OPTIONS]

        OPTIONS:
            --install          Install skill and rule files for AI coding agents
            -g, --global       Install to global assistant config directory (~/.claude, ~/.cursor, etc.)
                               instead of local project files
            --agent <TARGET>   Target agent to install for:
                               all (default), claude, cursor, windsurf, cline, antigravity,
                               copilot, kilocode, mimo, codex, gemini, opencode
            --repo <PATH>      Target repository root (default: nearest repository above cwd)
            -h, --help         Print help information

        EXAMPLES:
            csmesh skill                             # Print skill markdown to stdout
            csmesh skill --install                   # Install across all supported agents in current repo
            csmesh skill --install --global          # Install globally for all agents on this computer
            csmesh skill --install -g --agent cursor # Install globally for Cursor only
            csmesh skill --install --agent claude    # Install locally for Claude Code only
        """;

    public const string IndexHelp =
        """
        csmesh index - Build or refresh the symbol graph for the repository

        USAGE:
            csmesh index [OPTIONS]

        OPTIONS:
            --repo <PATH>      Repository root (default: nearest .sln/.slnx/.git above cwd)
            --debug            Print debug details during indexing to stderr
            --no-telemetry     Do not record this invocation
            -h, --help         Print help information

        NOTES:
            Build the solution first. Without bin/ assemblies many call sites cannot be bound
            and the resulting graph will be missing edges; 'index' reports the count.

        EXAMPLES:
            csmesh index
            csmesh index --repo ./src
        """;

    public const string TraceHelp =
        """
        csmesh trace - Trace execution paths through DI, MediatR, interfaces and routes

        USAGE:
            csmesh trace <Type.Member> [OPTIONS]

        ARGUMENTS:
            <Type.Member>      Target member to trace (e.g. PaymentController.Post)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 600, exits code 2 on overflow)
            --depth <N>        Maximum call chain depth (default: 6)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        EXAMPLES:
            csmesh trace PaymentController.Post --budget 600
            csmesh trace OrderService.Submit --depth 3
        """;

    public const string ImplHelp =
        """
        csmesh impl - Find implementations of an interface or base class, DI-bound first

        USAGE:
            csmesh impl <IInterface|BaseClass> [OPTIONS]

        ARGUMENTS:
            <IInterface>       Interface or abstract base type (e.g. IPaymentGateway)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 300, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        EXAMPLES:
            csmesh impl IPaymentGateway --budget 300
            csmesh impl PaymentHandlerBase
        """;

    public const string BlastRadiusHelp =
        """
        csmesh blast-radius - Discover callers and entrypoints affected by changing a symbol

        USAGE:
            csmesh blast-radius <Type.Member> [OPTIONS]

        ARGUMENTS:
            <Type.Member>      Target member to analyze (e.g. Order.Status)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 800, exits code 2 on overflow)
            --depth <N>        Maximum reverse traversal depth (default: 3)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        EXAMPLES:
            csmesh blast-radius Order.Status --budget 800
            csmesh blast-radius PaymentService.Process --depth 2
        """;

    public const string EntrypointsHelp =
        """
        csmesh entrypoints - Find HTTP endpoints, message handlers, consumers, and jobs

        USAGE:
            csmesh entrypoints [FILTER] [OPTIONS]

        ARGUMENTS:
            [FILTER]           Optional query filter (matches route, symbol name, or tag)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 600, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        EXAMPLES:
            csmesh entrypoints
            csmesh entrypoints payments
            csmesh entrypoints "POST /orders"
        """;

    public const string ContextHelp =
        """
        csmesh context - Everything structural about one symbol, in a single call

        USAGE:
            csmesh context <Type.Member> [OPTIONS]

        ARGUMENTS:
            <Type.Member>      Target symbol (e.g. PaymentService.Process)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 800, exits code 2 on overflow)
            --depth <N>        Reverse traversal depth for impact and entrypoints (default: 3)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Replaces chaining trace + impl + blast-radius + entrypoints when the question is
            "what is this thing and what touches it". Sections are ordered by usefulness, so an
            exhausted budget drops the tail rather than the answer.

        EXAMPLES:
            csmesh context PaymentService.Process --budget 800
            csmesh context IPaymentGateway --depth 2
        """;

    public const string PathHelp =
        """
        csmesh path - Shortest route from one symbol to another

        USAGE:
            csmesh path <From> <To> [OPTIONS]
            csmesh why <From> <To> [OPTIONS]

        ARGUMENTS:
            <From>             Where the request starts (e.g. PaymentController.Post)
            <To>               Where you want to know it arrives (e.g. StripeGateway.Authorize)

        OPTIONS:
            --budget <N>       Maximum output tokens (default: 400, exits code 2 on overflow)
            --depth <N>        Maximum hops to search (default: 12)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Answers "why is this class involved in this request". Mediator dispatch, container
            bindings and interface calls each count as one hop, so the path crosses them like any
            other edge. Exits 1 when no route exists within the depth limit.

        EXAMPLES:
            csmesh path PaymentController.Post StripeGateway.Authorize
            csmesh why OrderEndpoint OrderRepository --depth 6
        """;

    public const string CyclesHelp =
        """
        csmesh cycles - Circular dependencies between types or namespaces

        USAGE:
            csmesh cycles [OPTIONS]

        OPTIONS:
            --namespace        Collapse to namespace level instead of type level
            --project          Collapse to project level
            --budget <N>       Maximum output tokens (default: 800, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Recursion inside one type is not reported; only loops between types, or between
            namespaces, which are design problems rather than control flow.

        EXAMPLES:
            csmesh cycles
            csmesh cycles --namespace --budget 400
            csmesh cycles --project
        """;

    public const string UnresolvedHelp =
        """
        csmesh unresolved - Where the indexer failed, grouped by reason

        USAGE:
            csmesh unresolved [OPTIONS]

        OPTIONS:
            --kind <K>         Filter to one kind: call, type, di, mediatr
            --budget <N>       Maximum output tokens (default: 600, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            'doctor' reports that a graph is, say, 91% resolved. This says which 9%. A missing
            edge and an absent one look identical in every other command; this is the only place
            they can be told apart. The sample is capped at 400 sites per index.

        EXAMPLES:
            csmesh unresolved
            csmesh unresolved --kind di
            csmesh unresolved --kind mediatr --budget 300
        """;

    public const string DiffHelp =
        """
        csmesh diff - Symbols a git diff touched, and what they reach

        USAGE:
            csmesh diff [REF|RANGE] [OPTIONS]

        ARGUMENTS:
            [REF|RANGE]        What to diff against (default: HEAD, i.e. the working tree)

        OPTIONS:
            --staged           Diff the index instead of the working tree
            --depth <N>        Reverse traversal depth from each changed symbol (default: 3)
            --budget <N>       Maximum output tokens (default: 800, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            blast-radius takes a symbol; this takes a change-set. Changed lines are attributed to
            the innermost declaration that spans them, so an index built before the edit will
            attribute wrongly -- re-index first. Requires git on PATH.

        EXAMPLES:
            csmesh diff
            csmesh diff --staged
            csmesh diff main...HEAD --depth 2
        """;

    public const string ChangesHelp =
        """
        csmesh changes - Bindings, dispatches and implementations that appeared or vanished

        USAGE:
            csmesh changes [OPTIONS]

        OPTIONS:
            --calls            Include call and construction edges (noisy; off by default)
            --budget <N>       Maximum output tokens (default: 800, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Compares the current index against the one before it, kept at .csmesh/graph.prev.json.
            Run 'csmesh index' before your change and again after it.

            'diff' answers what you edited. This answers what the shape of the codebase did. A
            deleted AddScoped is one moved line in a diff and a missing binding here -- the
            compiler catches neither, and unit tests that inject mocks do not either.

        EXIT CODES:
            4 when there is no previous index to compare against.

        EXAMPLES:
            csmesh changes
            csmesh changes --calls --budget 1200
        """;

    public const string WhereHelp =
        """
        csmesh where - Find the symbols a word belongs to, ranked by what reaches them

        USAGE:
            csmesh where <term> [<term> ...] [OPTIONS]
            csmesh find <term> [OPTIONS]

        OPTIONS:
            --under <PATH>     Restrict to a subtree, e.g. --under src/Api
            --budget <N>       Maximum output tokens (default: 400, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Every other command takes a symbol. This is the one that finds it. Start here when the
            task is described in words -- "the discount rules", "checkout" -- and end with a name
            the other commands accept.

            Symbol names, namespaces, file paths and route templates are all searched. Results are
            ordered by how many entrypoints reach each symbol, not alphabetically: a request DTO
            and the handler that consumes it match the same word, and only one of them has three
            routes above it. Test code is ranked down, not hidden.

            The last line is the command to run next, already filled in.

            String literals, config values, TODOs and non-.cs files are not in the graph. Use grep
            for those.

        EXIT CODES:
            0 matches found   1 nothing matched   2 over budget

        EXAMPLES:
            csmesh where discount
            csmesh where checkout refund --under src/Application
            csmesh find "POST /orders"
        """;

    public const string MapHelp =
        """
        csmesh map - Where the weight is: projects, entrypoints, hotspots

        USAGE:
            csmesh map [OPTIONS]

        OPTIONS:
            --under <PATH>     Restrict to a subtree, e.g. --under src/Api
            --budget <N>       Maximum output tokens (default: 700, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            The first command to run in a repository you do not know. 'ls' and 'tree' answer
            "where are the files", which is the wrong axis: a folder name does not say whether
            anything in it is load bearing. This answers which projects lean on which, where the
            entrypoints cluster, and which handful of members everything runs through.

            Deliberately one screen. A map that needs two is a directory listing.

        EXAMPLES:
            csmesh map
            csmesh map --under src/Application --budget 400
        """;

    public const string SilenceHelp =
        """
        csmesh silence - Why a query came back empty

        USAGE:
            csmesh silence <symbol> [<target>] [OPTIONS]
            csmesh why-not <symbol> [<target>] [OPTIONS]

        ARGUMENTS:
            <symbol>           The symbol the answer was expected about
            [<target>]         Optional. With two symbols, explains a missing path between them

        OPTIONS:
            --depth <N>        How far to walk before giving up (default: 12)
            --budget <N>       Maximum output tokens (default: 700, exits code 2 on overflow)
            --json             Output as a structured JSON envelope
            --repo <PATH>      Repository root
            -h, --help         Print help information

        NOTES:
            Exit 1 from any other command means the graph had nothing. It does not say whether the
            symbol was mistyped, lives in a package, was never bound because the solution was not
            built, or is reached only through a container scan. Those call for four different next
            actions.

            With two symbols this reports where the walk stopped and what the indexer recorded at
            each dead end, and checks whether the route exists in the other direction. With one it
            reports why nothing enters or leaves it, including references that appear in source but
            never bound.

        EXIT CODES:
            0 when there was no silence to explain -- the path exists, or the symbol is connected.
            1 when the absence is real and explained.

        EXAMPLES:
            csmesh silence PaymentController.Post StripeGateway.Authorize
            csmesh silence IPaymentGateway
            csmesh why-not OrderService.Process AuditLog.Write --depth 6
        """;

    public const string UsageHelp =
        """
        csmesh usage - Display local invocation metrics and caller attribution

        USAGE:
            csmesh usage [OPTIONS]

        OPTIONS:
            --days <N>         Filter invocations from the last N days (default: 7)
            --tail <N>         Show the last N invocations instead of the summary (default: 10)
            --json             Output the raw invocation log as JSON
            --repo <PATH>      Repository root
            -h, --help         Print help information

        READING IT:
            The hit rate covers only commands that resolve a symbol, because exit 1 means
            something different elsewhere: from 'silence' it means a real absence was explained,
            from 'diff' that nothing changed.

            A high answered rate means the tool works and the question is whether anyone is
            reaching for it. A high not-found rate means it is being reached for and failing,
            which is a different problem. 'never used' is a read on the installed skill rather
            than on the commands themselves.

        EXAMPLES:
            csmesh usage
            csmesh usage --days 30
            csmesh usage --tail 20
        """;

    public const string DoctorHelp =
        """
        csmesh doctor - Diagnose index freshness, skill installation, and environment

        USAGE:
            csmesh doctor [OPTIONS]

        OPTIONS:
            --repo <PATH>      Repository root (default: nearest .sln/.slnx/.git above cwd)
            -h, --help         Print help information

        EXAMPLES:
            csmesh doctor
        """;

    public const string VersionHelp =
        """
        csmesh version - Print version information

        USAGE:
            csmesh version
            csmesh --version
            csmesh -v
        """;
}