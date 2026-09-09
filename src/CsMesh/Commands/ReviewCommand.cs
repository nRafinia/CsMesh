using System.Text.Json;
using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;

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
    public static int Execute(string root, Options opt)
    {
        var json = opt.Flag("json");
        var budget = opt.Int("budget", 800);
        var includeCalls = opt.Flag("calls");
        var accept = opt.Flag("accept");

        var writer = new BudgetWriter(budget);
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

        var findings = Queries.DiffFindings(current, baseGraph, includeCalls);
        var acceptedBefore = BaselineFile.Load(root);

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

        var cached = GraphStore.LoadBaseGraph(root, sha);
        if (cached != null)
        {
            graph = cached;
            return true;
        }

        EnsureGitIgnoreEntry(root);

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
            var built = Indexer.Build(worktreePath, message => Dbg.Log(message));
            if (built.Files.Count == 0)
            {
                error = $"base revision {sha} indexed to zero files. " +
                        "Check that the revision exists and contains C# sources.";
                return false;
            }

            GraphStore.SaveBaseGraph(root, sha, built);
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

    /// <summary>
    /// A worktree inside the repository is untracked noise in the user's own git status unless
    /// ignored -- exactly the kind of thing a tool meant to reduce noise should not cause.
    /// </summary>
    private static void EnsureGitIgnoreEntry(string root)
    {
        const string entry = ".csmesh/";
        var path = Path.Combine(root, ".gitignore");

        try
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, entry + Environment.NewLine);
                return;
            }

            var content = File.ReadAllText(path);
            var alreadyPresent = content.Split('\n')
                .Select(l => l.TrimEnd('\r').Trim())
                .Any(l => l is ".csmesh/" or ".csmesh");
            if (alreadyPresent) return;

            var prefix = content.Length == 0 || content.EndsWith('\n') ? "" : Environment.NewLine;
            File.AppendAllText(path, prefix + entry + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Dbg.Log($"could not update .gitignore: {ex.Message}");
        }
    }

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
        w.Force("");
        w.Force("OVER BUDGET: raise --budget, or narrow with --under.");

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
        return exitCode;
    }

    private static string FirstLine(string s)
    {
        var trimmed = s.Trim();
        var newline = trimmed.IndexOf('\n');
        return newline < 0 ? trimmed : trimmed[..newline].TrimEnd('\r');
    }
}
