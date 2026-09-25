namespace CsMesh.Models;

/// <summary>
/// What 'doctor' found, for a caller that is not a terminal.
///
/// Doctor's value is largely in its prose -- it explains that nothing came from bin/ and what to
/// run about it -- so the lines are kept verbatim, exactly as QueryResult keeps Text alongside
/// Rows. What is lifted out here is only the handful of facts a caller would branch on: whether
/// there is a usable index at all, whether it is behind the working tree, whether it was built by
/// this binary, and whether resolution was good enough to trust the edges.
/// </summary>
public sealed class DoctorReport
{
    public string Command { get; set; } = "doctor";
    public int Exit { get; set; }
    public string Root { get; set; } = string.Empty;

    /// <summary>False when there is no graph, or one this build refuses to read.</summary>
    public bool HasIndex { get; set; }

    /// <summary>Why there is no usable index. Null when there is one.</summary>
    public string? Problem { get; set; }

    public int Nodes { get; set; }
    public int Edges { get; set; }

    /// <summary>Files changed since the index was built. Non-zero means rows may be marked stale.</summary>
    public int StaleFiles { get; set; }

    public string BuiltByVersion { get; set; } = string.Empty;
    public string RunningVersion { get; set; } = string.Empty;

    /// <summary>True when the index was written by a build other than the one running.</summary>
    public bool VersionGap { get; set; }

    public int FormatVersion { get; set; }
    public string BuiltFromCommit { get; set; } = string.Empty;
    public DateTimeOffset? BuiltAt { get; set; }
    public int IncrementalRefreshes { get; set; }

    public int ReferenceCount { get; set; }
    public int RuntimeReferences { get; set; }
    public int OutputReferences { get; set; }
    public int OutputDirectories { get; set; }
    public int ReferencesFailed { get; set; }
    public bool ReferencesCapped { get; set; }

    /// <summary>
    /// Call sites Roslyn could not bind. Every one is a missing call edge, so a caller deciding
    /// whether to trust a trace should read this before the trace.
    /// </summary>
    public int UnresolvedCallSites { get; set; }

    public int GlobalUsingSources { get; set; }

    /// <summary>.razor and .cshtml files found under the root. See Graph.RazorFileCount.</summary>
    public int RazorFileCount { get; set; }

    /// <summary>Component types recovered from generated Razor sources. See Graph.RazorComponentsIndexed.</summary>
    public int RazorComponentsIndexed { get; set; }

    /// <summary>Generated sources skipped as stale relative to their .razor/.cshtml file. See Graph.RazorStaleSources.</summary>
    public int RazorStaleSources { get; set; }

    /// <summary>Non-Razor generated .g.cs files compiled into the graph. See Graph.GeneratedSourcesIndexed.</summary>
    public int GeneratedSourcesIndexed { get; set; }

    public List<string> SkippedProjects { get; set; } = [];
    public string ScopeDecision { get; set; } = string.Empty;

    /// <summary>Edge totals by kind, so a caller can see at a glance that dispatch resolved to nothing.</summary>
    public Dictionary<string, int> EdgesByKind { get; set; } = new();

    /// <summary>
    /// Installed rules blocks whose bytes differ from what this build would write, one path per
    /// file. A warning rather than an error: the block carries no version, so an older block and a
    /// hand-edited one are indistinguishable and neither changes the exit code. A path is absent
    /// when its file has no block, which is "not installed", not stale.
    /// </summary>
    public List<string> StaleInstructions { get; set; } = [];

    /// <summary>
    /// Projects whose compilation reported CS8795, the signature of source-generator output that
    /// never reached disk. A warning: the graph still answers, but every member the generator would
    /// have declared is absent from it.
    /// </summary>
    public List<GeneratedOutputFinding> MissingGeneratedOutput { get; set; } = [];

    /// <summary>
    /// PackageReferences whose Include names an in-scope project's package id. The package is never
    /// restored, so the referenced project's types are not bound through it; a ProjectReference is
    /// the fix.
    /// </summary>
    public List<ProjectPackageFinding> PackageReferencesToProjects { get; set; } = [];

    /// <summary>The report as a terminal would have shown it.</summary>
    public List<string> Text { get; set; } = [];
}

/// <summary>One PackageReference that names an in-scope project instead of a package.</summary>
public sealed class ProjectPackageFinding
{
    /// <summary>The project carrying the PackageReference, relative to the repository root.</summary>
    public string ReferencedFrom { get; set; } = string.Empty;

    /// <summary>The Include value that matched an in-scope project's package id.</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>The project that id belongs to, relative to the repository root.</summary>
    public string ReferencedProject { get; set; } = string.Empty;
}

/// <summary>One project whose compilation reported CS8795, with how many times.</summary>
public sealed class GeneratedOutputFinding
{
    /// <summary>The project file, relative to the repository root.</summary>
    public string Project { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>
/// One solution file that was found but did not fully decide scope: some of its project paths
/// matched nothing on disk, it listed no project path at all, or it could not be read. The partial
/// case still decided scope, so the finding records how many paths matched rather than claiming the
/// solution was ignored.
/// </summary>
public sealed class SolutionScopeFinding
{
    /// <summary>The solution file, relative to the repository root.</summary>
    public string Solution { get; set; } = string.Empty;

    /// <summary>Project paths the solution listed.</summary>
    public int Named { get; set; }

    /// <summary>Of those, how many named a project that exists on disk.</summary>
    public int Matched { get; set; }

    /// <summary>
    /// The first listed path that matched nothing, verbatim as the solution wrote it. Shown so a
    /// separator or a typo is visible at a glance; null when every listed path matched or none was
    /// listed.
    /// </summary>
    public string? FirstUnmatched { get; set; }

    /// <summary>Why the file could not be read. Null when it parsed.</summary>
    public string? ParseError { get; set; }
}

/// <summary>
/// What an index run did. Mirrors DoctorReport's split: the numbers a caller branches on, plus
/// the lines it would have read.
/// </summary>
public sealed class IndexReport
{
    public string Command { get; set; } = "index";
    public int Exit { get; set; }
    public string Root { get; set; } = string.Empty;

    /// <summary>full, incremental, or current when nothing needed doing.</summary>
    public string Mode { get; set; } = string.Empty;

    public int Nodes { get; set; }
    public int Edges { get; set; }
    public int Files { get; set; }

    /// <summary>Change in node and edge counts, when this run patched an existing graph.</summary>
    public int? NodeDelta { get; set; }

    public int? EdgeDelta { get; set; }

    public int ReboundFiles { get; set; }
    public double ElapsedSeconds { get; set; }
    public int UnresolvedCallSites { get; set; }
    public int ReferenceCount { get; set; }
    public string BuiltByVersion { get; set; } = string.Empty;

    public List<string> Text { get; set; } = [];
}
