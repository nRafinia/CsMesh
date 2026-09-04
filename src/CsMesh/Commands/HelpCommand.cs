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
            index          Build or refresh the symbol graph for the repository
            trace          Trace execution paths through DI, MediatR, and interfaces
            impl           Find implementations of an interface or base class, DI-bound first
            blast-radius   Discover callers and entrypoints affected by changing a symbol
            entrypoints    Find HTTP endpoints, message handlers, consumers, and jobs
            context        Everything structural about one symbol, in a single call
            path           Shortest route from one symbol to another (alias: why)
            cycles         Circular dependencies between types or namespaces
            usage          Display local invocation metrics and caller attribution
            doctor         Diagnose index freshness, skill installation, and environment
            skill          Display skill markdown or install agent skill/rule files
            version        Print version information
            help           Print this message or the help of the given subcommand(s)

        GLOBAL OPTIONS:
            --repo <PATH>      Repository root (default: nearest .sln/.slnx/.git above cwd)
            --budget <N>       Maximum output tokens (trace 600, impl 300, blast-radius 800)
            --depth <N>        Traversal depth limit (trace 6, blast-radius 3, context 3, path 12)
            --json             Output results as a structured JSON envelope
            --debug            Enable verbose diagnostics on stderr
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
