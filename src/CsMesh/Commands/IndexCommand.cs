using System.Text.Json;
using System.Diagnostics;
using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;

namespace CsMesh.Commands;

public static class IndexCommand
{
    public static int Execute(string root, Options opt)
    {
        var clock = Stopwatch.StartNew();
        var e = new Emit(opt.Flag("json"));
        var report = new IndexReport { Root = root };

        if (!opt.Flag("full") && TryIncremental(root, opt, clock, e, report, out var patched))
        {
            Finish(report, e, patched);
            return patched;
        }

        var graph = Indexer.Build(root, message => Dbg.Log(message), opt.Flag("all"));
        GraphStore.Save(graph);

        report.Mode = "full";
        report.Nodes = graph.Nodes.Count;
        report.Edges = graph.Edges.Count;
        report.Files = graph.Files.Count;
        report.UnresolvedCallSites = graph.UnresolvedCallSites;
        report.ReferenceCount = graph.ReferenceCount;
        report.BuiltByVersion = graph.BuiltByVersion;
        report.ElapsedSeconds = clock.Elapsed.TotalSeconds;

        e.Line($"indexed {graph.Files.Count} files -> {graph.Nodes.Count} nodes, " +
                          $"{graph.Edges.Count} edges in {clock.Elapsed.TotalSeconds:F1}s");

        ReportStandingConditions(graph, fromFullIndex: true, e);

        if (Dbg.On)
        {
            foreach (var group in graph.Edges.GroupBy(edge => edge.Kind).OrderByDescending(x => x.Count()))
            {
                Dbg.Log($"edges {group.Key}: {group.Count()}");
            }

            foreach (var node in graph.Nodes.Where(n => n.Tags.Count > 0).Take(15))
            {
                Dbg.Log($"tag {node.Short}: {string.Join(",", node.Tags)}");
            }
        }

        Finish(report, e, Exit.Ok);
        return Exit.Ok;
    }

    /// <summary>
    /// Serialises the report when the caller asked for JSON. Called on every return path, because
    /// an index that took the incremental branch is exactly the run a caller most wants the
    /// numbers from.
    /// </summary>
    private static void Finish(IndexReport report, Emit e, int exit)
    {
        report.Exit = exit;
        report.Text.AddRange(e.Lines);

        if (e.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, AppJsonContext.Default.IndexReport));
        }
    }

    /// <summary>
    /// Re-binds the edited files against the existing graph. Returns false whenever the full path
    /// should run instead, which is the default answer for anything unusual: a missing or
    /// incompatible graph, too many edits at once, or an edit that touches a construct bound
    /// across file boundaries.
    ///
    /// This is the common case in an agent session and it is the case the old behaviour handled
    /// worst -- two or three files edited since the last index, and a full solution rebuild as the
    /// only way to clear [STALE].
    /// </summary>
    private static bool TryIncremental(string root, Options opt, Stopwatch clock, Emit e, IndexReport report, out int exit)
    {
        exit = Exit.Ok;

        var existing = GraphStore.Load(root, out var problem);
        if (existing == null)
        {
            Dbg.Log($"incremental skipped: no usable graph ({problem})");
            return false;
        }

        // --all changes which projects are in scope, and dropping it changes them back. Only the
        // widening direction used to be checked, so a graph built with --all was reported as
        // current for a plain 'csmesh index' -- the answer covered files the caller had just asked
        // to exclude, and every later run agreed with it because nothing had changed on disk.
        if (opt.Flag("all") != existing.IndexedAllProjects) return false;

        // A graph written by a different build is not patchable and, more importantly, is not
        // current. Most releases change what the indexer notices -- a DI shape it now recognises,
        // a false positive it no longer emits -- without moving FormatVersion, so this file would
        // otherwise deserialize cleanly and be announced as up to date, and the user would go on
        // querying the old binary's answers from the new binary. Rebuild and say so.
        if (GraphStore.BuiltByOtherVersion(existing))
        {
            e.Line($"re-indexing in full: {GraphStore.VersionGap(existing)}");
            return false;
        }

        var dirty = GraphStore.DirtyFiles(existing);
        if (dirty.Count == 0)
        {
            report.Mode = "current";
            report.Nodes = existing.Nodes.Count;
            report.Edges = existing.Edges.Count;
            report.Files = existing.Files.Count;
            report.UnresolvedCallSites = existing.UnresolvedCallSites;
            report.ReferenceCount = existing.ReferenceCount;
            report.BuiltByVersion = existing.BuiltByVersion;
            report.ElapsedSeconds = clock.Elapsed.TotalSeconds;

            e.Line($"index is current: {existing.Nodes.Count} nodes, {existing.Edges.Count} edges, " +
                              $"built {Ago(existing.BuiltAt)}");
            ReportStandingConditions(existing, fromFullIndex: existing.IncrementalRefreshes == 0, e);
            return true;
        }

        var before = (Nodes: existing.Nodes.Count, Edges: existing.Edges.Count);
        var patched = Indexer.BuildIncremental(existing, dirty, message => Dbg.Log(message));
        if (patched == null) return false;

        // The previous snapshot is deliberately not rotated here. 'changes' compares against the
        // last full index; rotating on every small patch would leave it comparing a graph to
        // itself and reporting that nothing structural moved -- which is the one answer it must
        // never give wrongly.
        GraphStore.SaveInPlace(patched);

        var dn = patched.Nodes.Count - before.Nodes;
        var de = patched.Edges.Count - before.Edges;

        report.Mode = "incremental";
        report.Nodes = patched.Nodes.Count;
        report.Edges = patched.Edges.Count;
        report.Files = patched.Files.Count;
        report.NodeDelta = dn;
        report.EdgeDelta = de;
        report.ReboundFiles = dirty.Count;
        report.UnresolvedCallSites = patched.UnresolvedCallSites;
        report.ReferenceCount = patched.ReferenceCount;
        report.BuiltByVersion = patched.BuiltByVersion;
        report.ElapsedSeconds = clock.Elapsed.TotalSeconds;

        e.Line($"rebound {dirty.Count} file(s) -> {patched.Nodes.Count} nodes ({Delta(dn)}), " +
                          $"{patched.Edges.Count} edges ({Delta(de)}) in {clock.Elapsed.TotalSeconds:F1}s");

        ReportStandingConditions(patched, fromFullIndex: false, e);
        return true;
    }

    /// <summary>
    /// The conditions that are true of the index regardless of what this run did.
    ///
    /// Four projects outside the solution and eleven call sites that will not bind are facts about
    /// the repository, not events belonging to a full rebuild -- but they were printed only by the
    /// full path, so the moment indexing became incremental they went quiet. What replaced them was
    /// a single line saying the index was current, which is a much weaker claim than it reads as:
    /// the index is as complete as it was, and it was never complete.
    /// </summary>
    private static void ReportStandingConditions(Graph graph, bool fromFullIndex, Emit e)
    {
        if (graph.SkippedProjects.Count > 0)
        {
            e.Line($"skipped {graph.SkippedProjects.Count} project(s): {graph.SkippedProjectsReason}");
            foreach (var project in graph.SkippedProjects.Take(5))
            {
                e.Line($"  {project}");
            }

            if (graph.SkippedProjects.Count > 5)
            {
                e.Line($"  ... and {graph.SkippedProjects.Count - 5} more; see csmesh doctor");
            }

            e.Line("  index them anyway with: csmesh index --all");
        }

        // Unbound call sites are missing edges, not cosmetic warnings. Say so at index time rather
        // than letting a thin answer look like a complete one.
        if (graph.UnresolvedCallSites == 0) return;

        var age = fromFullIndex ? "" : " (from the last full index)";
        e.Line($"warning: {graph.UnresolvedCallSites} call site(s) could not be bound " +
                          $"against {graph.ReferenceCount} references{age}.");

        // Naming the build as the cause when bin/ already holds hundreds of assemblies is a wrong
        // diagnosis stated with confidence, and it sent one investigation down the wrong path.
        e.Line(graph.OutputReferences == 0
            ? "         Nothing was loaded from bin/. Run 'dotnet build', then index again."
            : "         Run 'csmesh doctor' for what the compiler said about them.");
    }

    private static string Delta(int n) => n >= 0 ? $"+{n}" : n.ToString();

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}