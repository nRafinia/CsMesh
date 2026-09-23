namespace CsMesh.Models;

/// <summary>
/// One emitted answer row. Carries the same information as the text line, in a shape agents can
/// branch on without parsing markers out of a string.
/// </summary>
public sealed class QueryRow
{
    /// <summary>Distance from the queried symbol. 0 is the symbol itself.</summary>
    public int Depth { get; set; }

    public string Symbol { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// How this row relates to its parent: call, mediatr, impl, override, new, route,
    /// entrypoint, direct-caller, indirect, candidate, suggestion.
    /// </summary>
    public string? Relation { get; set; }

    /// <summary>Edge annotation, e.g. di-bound or via Send(CreatePaymentCommand).</summary>
    public string? Note { get; set; }

    public string? File { get; set; }
    public int Line { get; set; }

    /// <summary>True when the source file changed after the index was built.</summary>
    public bool Stale { get; set; }

    /// <summary>
    /// How far the edge that produced this row can be trusted, 0 to 1. Absent means certain.
    /// Below 0.8 the caller should open the file before acting on the row.
    /// </summary>
    public double? Confidence { get; set; }

    /// <summary>What produced the edge, when it was not a plain compiler symbol lookup.</summary>
    public string? Source { get; set; }

    /// <summary>Where the edge was declared, as file:line. Null for definition-only rows.</summary>
    public string? Site { get; set; }

    /// <summary>
    /// The stable finding id 'review' derives from the edge signature. Null outside 'review', where
    /// nothing needs an identity that survives being asked about twice.
    /// </summary>
    public string? Id { get; set; }

    /// <summary>Owning project's root-relative directory, as on <see cref="Node.Project"/>.</summary>
    public string? Project { get; set; }

    public List<string>? Tags { get; set; }
}

/// <summary>
/// Envelope returned by <c>--json</c>. <see cref="Exit"/> mirrors the process exit code so a
/// captured payload stays self-describing.
/// </summary>
public sealed class QueryResult
{
    public string Command { get; set; } = string.Empty;
    public string? Query { get; set; }
    public int Exit { get; set; }

    /// <summary>True when the answer was cut short by the token budget.</summary>
    public bool Truncated { get; set; }

    /// <summary>Number of files that changed since the index was built.</summary>
    public int StaleFiles { get; set; }

    public List<string> Notes { get; set; } = [];

    /// <summary>
    /// 'review' only: the reference inputs (project, props, targets, solution, packages.lock.json,
    /// global.json) that changed between the base and the working tree. Non-empty means the graph
    /// comparison may be looking past a dependency change the edges do not capture -- the command
    /// still reports normally, but a caller should read the finding with that in mind. Null when
    /// nothing changed so the field is absent from the envelope rather than an empty list on every
    /// command.
    /// </summary>
    public List<string>? ReferenceInputsChanged { get; set; }

    /// <summary>
    /// 'blast-radius --writes' on an event: its += / -= edges are Subscribe, not Write, so no writer
    /// rows are due. The hint names the command that does show its subscribers. Text mode prints it;
    /// JSON carries it here instead of duplicating it in <see cref="Text"/>.
    /// </summary>
    public string? Hint { get; set; }

    /// <summary>
    /// The answer as it was rendered for a terminal, line by line.
    ///
    /// Rows carry the structured part, but not every command has one: 'silence' explains why a
    /// walk stopped, 'map' groups projects and entrypoints, 'changes' warns about a binding that
    /// vanished. All of that lives in prose, and a JSON consumer that only reads Rows would get an
    /// envelope with an exit code and nothing to act on.
    /// </summary>
    public List<string> Text { get; set; } = [];
    public List<QueryRow> Rows { get; set; } = [];
}
