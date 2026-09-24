using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using CsMesh.Telemetry;

namespace CsMesh.Commands;

/// <summary>
/// Compares the working tree against a git revision instead of against the last index, and gates
/// on the result the way changes/diff never needed to.
///
/// 'changes' answers "what moved since I last ran csmesh index", which is an accident of when that
/// happened to be. 'review' answers "what moved since this revision", which is the question a pull
/// request or a CI gate actually asks, and adds the two things a gate needs that a desk command
/// does not: a baseline of accepted findings, and an exit code that means something went wrong
/// rather than merely that an answer could not be produced.
/// </summary>
public static class ReviewCommand
{
    /// <summary>
    /// The budget <c>review</c> runs at when the caller does not pass <c>--budget</c>. Named rather
    /// than inline so the documented default can be pinned to it, which <see cref="QueryCommand"/>
    /// commands get for free through <c>WriterFor</c> and review does not.
    /// </summary>
    internal const int DefaultBudget = 800;

    public static int Execute(string root, Options opt)
    {
        var json = opt.Flag("json");
        var budget = opt.Int("budget", DefaultBudget);
        CsMesh.Telemetry.Telemetry.Current.Budget = budget;
        var includeCalls = opt.Flag("calls");
        var accept = opt.Flag("accept");

        var writer = new BudgetWriter(budget, BudgetWriter.CompletionMarkerReserve);
        var result = new QueryResult { Command = "review" };

        if (!GitTool.TryRun(root, "rev-parse --is-inside-work-tree", out _, out var repoError, out _))
        {
            return Fail(result, writer, json, Exit.Usage,
                $"not a git repository (or git is unavailable) at '{root}': {FirstLine(repoError)}");
        }

        if (!TryResolveBase(root, opt.Positional.FirstOrDefault(), out var baseRef, out var sha, out var resolveError))
        {
            return Fail(result, writer, json, Exit.Usage, resolveError);
        }

        var baseDescription = string.Equals(baseRef, sha, StringComparison.Ordinal) ? sha : $"{baseRef} ({sha})";
        result.Query = baseDescription;

        if (!TryGetBaseGraph(root, sha, out var baseGraph, out var baseGraphError))
        {
            return Fail(result, writer, json, Exit.NoIndex, baseGraphError);
        }

        var current = GraphStore.Load(root, out var problem);
        if (current == null)
        {
            return Fail(result, writer, json, Exit.NoIndex, $"no usable index ({problem}). run: csmesh index");
        }

        if (RefuseOnCommitGap(root, current, accept, result, writer, json) is { } gapExit)
        {
            return gapExit;
        }

        // Finding ids hash the two Node.Keys of an edge, so a format bump that re-keys the graph
        // changes every id. A baseline written under an older format would re-surface its whole
        // history as unaccepted; refusing is the honest answer. --accept is the remedy here, unlike
        // the commit gap, because writing the new ids is exactly what the user needs.
        var acceptedBefore = BaselineFile.Load(root, out var baselineFormat);
        if (BaselineFile.Exists(root) && baselineFormat != Graph.CurrentFormatVersion && !accept)
        {
            return Fail(result, writer, json, Exit.NoIndex,
                $"baseline predates format v{Graph.CurrentFormatVersion} -> csmesh review --accept");
        }

        // A reference input that changed in the range is a change the graph cannot see as an edge:
        // the package set moved, but no symbol did. Report normally and say so out loud rather than
        // guess, so the comparison is not mistaken for exhaustive.
        var referenceInputs = ChangedReferenceInputs(root, sha);
        if (referenceInputs.Count > 0)
        {
            result.ReferenceInputsChanged = referenceInputs;
            if (!json) Console.Error.WriteLine(ReferenceWarning(referenceInputs));
        }

        var findings = Queries.DiffFindings(current, baseGraph, includeCalls);

        var pruned = 0;
        if (accept)
        {
            pruned = BaselineFile.Save(root, findings.Select(f => (f.Id, Describe(f))));
        }

        var unaccepted = accept
            ? []
            : findings.Where(f => !acceptedBefore.Contains(f.Id)).ToList();

        if (!Render(writer, unaccepted, findings.Count, acceptedBefore.Count, baseDescription, accept, pruned))
        {
            return Truncated(result, writer, json);
        }

        var exitCode = accept ? Exit.Ok : unaccepted.Count > 0 ? Exit.Changed : Exit.Ok;

        if (json) return EmitJson(result, writer, exitCode);

        writer.Flush();
        return exitCode;
    }

    // ------------------------------------------------------------------ base revision resolution

    /// <summary>
    /// Resolves the base argument to a full commit and its identity for caching. An explicit
    /// argument is used as-is, exactly as 'diff' treats a positional range; the default is the
    /// merge base with the remote's default branch, falling back to HEAD when there is no remote
    /// or no merge base can be found -- both ordinary states for a repository with no origin
    /// configured, not failures.
    /// </summary>
    private static bool TryResolveBase(string root, string? explicitBase, out string baseRef, out string sha, out string error)
    {
        baseRef = explicitBase ?? DefaultBaseRef(root);
        sha = "";
        error = "";

        if (!GitTool.TryRun(root, $"rev-parse --verify \"{baseRef}^{{commit}}\"", out var stdout, out var stderr, out _))
        {
            error = $"base revision '{baseRef}' does not resolve: {FirstLine(stderr)}";
            return false;
        }

        var full = stdout.Trim();
        sha = full.Length > 12 ? full[..12] : full;
        return true;
    }

    /// <summary>
    /// origin/HEAD is a local symbolic ref once fetched, so this costs no network round trip. No
    /// remote, a remote with no HEAD set, or no merge base at all with the working tree's history
    /// (a shallow clone, an unrelated branch) all fall back to HEAD, i.e. the working tree's own
    /// parent -- which is always resolvable and is what a caller means by "since my last commit"
    /// when there is nothing more specific to compare against.
    /// </summary>
    private static string DefaultBaseRef(string root)
    {
        if (GitTool.TryRun(root, "symbolic-ref --short refs/remotes/origin/HEAD", out var originHead, out _, out _))
        {
            var branch = originHead.Trim();
            if (branch.Length > 0 &&
                GitTool.TryRun(root, $"merge-base HEAD {branch}", out var mergeBase, out _, out _) &&
                mergeBase.Trim().Length > 0)
            {
                return mergeBase.Trim();
            }
        }

        return "HEAD";
    }

    /// <summary>
    /// Refuses the run when the current index cannot be shown to be at HEAD, returning the exit code
    /// to stop with, or null when the review may proceed.
    /// </summary>
    /// <remarks>
    /// Both sides of a review are graphs; the working tree is never read, so working-tree staleness
    /// is the wrong question here. The one that matters is whether the index standing in for
    /// "current" was built at HEAD. When it was not, the comparison is wrong rather than thin: the
    /// index may never have seen the change under review, or may still hold one that was reverted.
    /// A warning would let a gate pass the very change it exists to catch, so the command refuses
    /// instead.
    ///
    /// The exit is <see cref="Exit.NoIndex"/>: the index exists but cannot answer this question,
    /// which is what 4 already tells a caller to fix with 'csmesh index'. <see cref="Exit.Changed"/>
    /// is not reused -- 5 gates a merge, so a gap would block for the right outcome but the wrong
    /// reason, and a caller treating 5 as a findings list would act on a list that does not exist.
    /// --accept under a gap is worse still: it writes findings from the wrong current side into the
    /// baseline, and every later review inherits the error silently, so it is a usage error (64).
    ///
    /// Auto-healing is deliberately not offered. Review already builds a base graph in a disposable
    /// worktree; silently re-indexing the current side would turn a read command into one that
    /// rewrites the user's own index as a side effect, at exactly the moment the user's assumptions
    /// are already wrong. The remedy is one command and the message names it.
    ///
    /// An empty <see cref="Graph.BuiltFromCommit"/> takes the same path: an index that records no
    /// commit cannot be shown current, and silence there is the same failure in a different costume.
    /// </remarks>
    private static int? RefuseOnCommitGap(
        string root, Graph current, bool accept, QueryResult result, BudgetWriter writer, bool json)
    {
        var gap = CommitGapNotice(root, current);
        if (gap == null) return null;

        return accept
            ? Fail(result, writer, json, Exit.Usage,
                gap + ". --accept is refused: it would record findings from the wrong current side")
            : Fail(result, writer, json, Exit.NoIndex, gap);
    }

    /// <summary>Describes why the current index is not at HEAD, naming the remedy, or null.</summary>
    private static string? CommitGapNotice(string root, Graph current)
    {
        const string remedy = " run: csmesh index, then re-run review";

        var built = current.BuiltFromCommit;
        if (built.Length == 0)
        {
            return $"# this index records no commit, so it cannot be shown current;{remedy}";
        }

        if (!GitTool.TryRun(root, "rev-parse HEAD", out var headOut, out _, out _)) return null;
        var head = headOut.Trim();
        if (head.Length == 0) return null;

        if (head.StartsWith(built, StringComparison.OrdinalIgnoreCase)) return null;

        var shortHead = head.Length > built.Length ? head[..built.Length] : head;
        return $"# index built at {built}, HEAD is {shortHead};{remedy}";
    }

    // ------------------------------------------------------------------ reference inputs

    /// <summary>Files that name or configure the reference set: changing one can change which
    /// packages and projects the build resolves without moving a symbol the graph records.</summary>
    private static bool IsReferenceInput(string path)
    {
        var name = Path.GetFileName(path);
        if (name is "Directory.Packages.props" or "packages.lock.json" or "global.json") return true;
        return Path.GetExtension(name) is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";
    }

    private static List<string> ChangedReferenceInputs(string root, string sha)
    {
        if (!GitTool.TryRun(root, $"diff --name-only {sha}", out var stdout, out _, out _)) return [];

        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Replace('\\', '/'))
            .Where(IsReferenceInput)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One warning line: at most five names, then a count, so a rename of a big props file
    /// cannot flood the terminal.</summary>
    private static string ReferenceWarning(List<string> changed)
    {
        var shown = string.Join(", ", changed.Take(5));
        var more = changed.Count > 5 ? $" (+{changed.Count - 5} more)" : "";
        return $"warning: reference inputs changed in this range: {shown}{more}. "
             + "The base is still compiled against the working tree's reference set.";
    }

    // ------------------------------------------------------------------ base graph, cached per commit

    /// <summary>
    /// Reuses a cached graph for this commit when there is one; otherwise checks the commit out
    /// into a disposable worktree, indexes it, caches the result, and removes the worktree. Never
    /// touches the caller's own working tree: a worktree is a separate checkout, so uncommitted
    /// changes in the tree this command is being run against are never at risk.
    /// </summary>
    private static bool TryGetBaseGraph(string root, string sha, out Graph graph, out string error)
    {
        graph = null!;
        error = "";

        // The reference set is part of the cache key: the same commit indexed against a different
        // bin/ is a different graph, and reusing the thin one is the false exit 5 this prevents.
        var referenceKey = GraphStore.ReferenceKeyFor(root);
        var cached = GraphStore.LoadBaseGraph(root, sha, referenceKey);
        if (cached != null)
        {
            graph = cached;
            return true;
        }

        CsMeshDir.Ensure(root);

        var worktreePath = GraphStore.BaseWorktreePath(root);
        CleanupWorktree(root, worktreePath);

        var relative = ToGitRelativePath(root, worktreePath);
        if (!GitTool.TryRun(root, $"worktree add --detach \"{relative}\" {sha}", out _, out var addError, out _))
        {
            error = $"could not create a worktree at '{relative}' for {sha}: {FirstLine(addError)}";
            return false;
        }

        try
        {
            // The base is compiled against the working tree's built reference set, not the clean
            // worktree's empty bin/. Indexing it against its own tree leaves package types unbound,
            // which drops DI/MediatR/route edges and shifts symbol keys -- a false exit 5 on a tree
            // that did not change.
            var built = Indexer.Build(worktreePath, message => Dbg.Log(message), referenceRoot: root);
            if (built.Files.Count == 0)
            {
                error = $"base revision {sha} indexed to zero files. " +
                        "Check that the revision exists and contains C# sources.";
                return false;
            }

            GraphStore.SaveBaseGraph(root, sha, referenceKey, built);

            // Only after the write is known to have landed: the newly cached key is the one the
            // next run reuses, and this revision's other keys are full-solution graphs no run with
            // this reference set can. Best effort by construction -- pruning never changes the exit
            // code of the review that just succeeded.
            GraphStore.PruneBaseGraphsForRevision(root, sha, referenceKey);

            graph = built;
            return true;
        }
        catch (Exception ex)
        {
            error = $"could not index base revision {sha}: {ex.Message}";
            return false;
        }
        finally
        {
            CleanupWorktree(root, worktreePath);

            // The count rule runs on the way out, after any write above, and deletes by write time.
            // The file just written is the most recent, so it is never the one evicted.
            GraphStore.PruneBaseGraphs(root);
        }
    }

    /// <summary>
    /// Best effort in both directions: removes a worktree git still has registered whether or not
    /// its directory survives, and removes a leftover directory whether or not git still has it
    /// registered. Either half can be the one left behind by a process that died mid-command, and
    /// neither failing is a reason to fail this command -- 'worktree add' below is what actually
    /// needs a clean slate, and it will say so plainly if this left something in its way.
    /// </summary>
    private static void CleanupWorktree(string root, string worktreePath)
    {
        var relative = ToGitRelativePath(root, worktreePath);
        GitTool.TryRun(root, $"worktree remove --force \"{relative}\"", out _, out _, out _);

        if (Directory.Exists(worktreePath))
        {
            try { Directory.Delete(worktreePath, recursive: true); }
            catch (Exception ex) { Dbg.Log($"could not delete leftover worktree '{worktreePath}': {ex.Message}"); }
        }

        GitTool.TryRun(root, "worktree prune", out _, out _, out _);
    }

    private static string ToGitRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    // ------------------------------------------------------------------ baseline text

    private static string Describe(Queries.ReviewFinding f)
    {
        var parts = new List<string> { f.Relation, f.Kind.ToString(), $"{f.From} -> {f.To}" };
        if (f.Note != null) parts.Add($"[{f.Note}]");
        if (f.Site != null) parts.Add($"@ {f.Site}");
        return string.Join("  ", parts);
    }

    // ------------------------------------------------------------------ rendering

    /// <summary>Returns false when the budget was exceeded before everything fit.</summary>
    private static bool Render(
        BudgetWriter w, List<Queries.ReviewFinding> unaccepted, int totalFindings, int acceptedCount,
        string baseDescription, bool didAccept, int pruned)
    {
        if (didAccept)
        {
            var prunedNote = pruned > 0 ? $"; pruned {pruned} dead entr{(pruned == 1 ? "y" : "ies")}" : "";
            w.Force($"accepted {totalFindings} finding(s) against {baseDescription}{prunedNote}.");
            return true;
        }

        if (unaccepted.Count == 0)
        {
            w.Force(totalFindings == 0
                ? $"no structural change against {baseDescription}."
                : $"no unaccepted structural change against {baseDescription} ({totalFindings} accepted, not shown).");
            return true;
        }

        w.Force($"{unaccepted.Count} unaccepted finding(s) against {baseDescription}"
                + (acceptedCount > 0 ? $" ({acceptedCount} accepted, not shown)" : "") + ".");

        foreach (var relationGroup in unaccepted.GroupBy(f => f.Relation).OrderBy(g => RelationRank(g.Key)))
        {
            if (!w.Add("")) return false;
            if (!w.Add(relationGroup.Key.ToUpperInvariant())) return false;

            foreach (var kindGroup in relationGroup.GroupBy(f => f.Kind).OrderBy(g => Queries.StructuralRank(g.Key)))
            {
                if (!w.Add($"  {kindGroup.Key}  ({kindGroup.Count()})")) return false;

                foreach (var f in kindGroup)
                {
                    var note = f.Note != null ? $"  [{f.Note}]" : "";
                    var site = f.Site != null ? $"  @ {f.Site}" : "";

                    var row = new QueryRow
                    {
                        Depth = 1,
                        Symbol = $"{f.From} -> {f.To}",
                        Kind = f.Kind.ToString(),
                        Relation = f.Relation,
                        Note = f.Note,
                        Site = f.Site,
                        Id = f.Id,
                        Confidence = f.Score < 1.0 ? f.Score : null
                    };

                    if (!w.Add($"    {f.From} -> {f.To}{note}{site}  #{f.Id}", row)) return false;
                }
            }
        }

        if (!w.Add("")) return false;
        if (!w.Add("Reviewed and fine to keep: csmesh review --accept")) return false;

        return true;
    }

    private static int RelationRank(string relation) => relation switch
    {
        "removed" => 0,
        "added" => 1,
        "degraded" => 2,
        _ => 3
    };

    // ------------------------------------------------------------------ output plumbing

    private static int Fail(QueryResult result, BudgetWriter writer, bool json, int exitCode, string message)
    {
        if (json)
        {
            result.Notes.Add(message);
            return EmitJson(result, writer, exitCode);
        }

        Console.Error.WriteLine(message);
        return exitCode;
    }

    /// <summary>Mirrors Queries.Diff.Truncated: the overflow warning is always forced into the
    /// output, in either text or JSON mode, rather than sent only to stderr like an upfront
    /// failure -- everything gathered so far is still worth keeping.</summary>
    private static int Truncated(QueryResult result, BudgetWriter w, bool json)
    {
        w.AddMarker(Queries.IncompleteMarker(w, "raise --budget, or narrow with --under"));

        if (json) return EmitJson(result, w, Exit.OverBudget);

        w.Flush();
        return Exit.OverBudget;
    }

    private static int EmitJson(QueryResult result, BudgetWriter writer, int exitCode)
    {
        result.Rows.AddRange(writer.Rows);
        result.Text.AddRange(writer.Lines);
        result.Exit = exitCode;
        result.Truncated = writer.Overflowed;

        Console.WriteLine(JsonSerializer.Serialize(result, AppJsonContext.Default.QueryResult));
        CsMesh.Telemetry.Telemetry.Current.OutTokens = writer.Tokens;
        CsMesh.Telemetry.Telemetry.Current.WouldBeTokens = writer.WouldBeTokens;
        return exitCode;
    }

    private static string FirstLine(string s)
    {
        var trimmed = s.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd('\r');
    }
}
