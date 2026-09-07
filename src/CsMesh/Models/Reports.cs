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

    public List<string> SkippedProjects { get; set; } = [];
    public string ScopeDecision { get; set; } = string.Empty;

    /// <summary>Edge totals by kind, so a caller can see at a glance that dispatch resolved to nothing.</summary>
    public Dictionary<string, int> EdgesByKind { get; set; } = new();

    /// <summary>The report as a terminal would have shown it.</summary>
    public List<string> Text { get; set; } = [];
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
