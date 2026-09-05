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

        if (!opt.Flag("full") && TryIncremental(root, opt, clock, out var patched))
        {
            return patched;
        }

        var graph = Indexer.Build(root, message => Dbg.Log(message), opt.Flag("all"));
        GraphStore.Save(graph);

        Console.WriteLine($"indexed {graph.Files.Count} files -> {graph.Nodes.Count} nodes, " +
                          $"{graph.Edges.Count} edges in {clock.Elapsed.TotalSeconds:F1}s");

        ReportStandingConditions(graph, fromFullIndex: true);

        if (Dbg.On)
        {
            foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()))
            {
                Dbg.Log($"edges {group.Key}: {group.Count()}");
            }

            foreach (var node in graph.Nodes.Where(n => n.Tags.Count > 0).Take(15))
            {
                Dbg.Log($"tag {node.Short}: {string.Join(",", node.Tags)}");
            }
        }

        return Exit.Ok;
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
    private static bool TryIncremental(string root, Options opt, Stopwatch clock, out int exit)
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

        var dirty = GraphStore.DirtyFiles(existing);
        if (dirty.Count == 0)
        {
            Console.WriteLine($"index is current: {existing.Nodes.Count} nodes, {existing.Edges.Count} edges, " +
                              $"built {Ago(existing.BuiltAt)}");
            ReportStandingConditions(existing, fromFullIndex: existing.IncrementalRefreshes == 0);
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

        Console.WriteLine($"rebound {dirty.Count} file(s) -> {patched.Nodes.Count} nodes ({Delta(dn)}), " +
                          $"{patched.Edges.Count} edges ({Delta(de)}) in {clock.Elapsed.TotalSeconds:F1}s");

        ReportStandingConditions(patched, fromFullIndex: false);
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
    private static void ReportStandingConditions(Graph graph, bool fromFullIndex)
    {
        if (graph.SkippedProjects.Count > 0)
        {
            Console.WriteLine($"skipped {graph.SkippedProjects.Count} project(s): {graph.SkippedProjectsReason}");
            foreach (var project in graph.SkippedProjects.Take(5))
            {
                Console.WriteLine($"  {project}");
            }

            if (graph.SkippedProjects.Count > 5)
            {
                Console.WriteLine($"  ... and {graph.SkippedProjects.Count - 5} more; see csmesh doctor");
            }

            Console.WriteLine("  index them anyway with: csmesh index --all");
        }

        // Unbound call sites are missing edges, not cosmetic warnings. Say so at index time rather
        // than letting a thin answer look like a complete one.
        if (graph.UnresolvedCallSites == 0) return;

        var age = fromFullIndex ? "" : " (from the last full index)";
        Console.WriteLine($"warning: {graph.UnresolvedCallSites} call site(s) could not be bound " +
                          $"against {graph.ReferenceCount} references{age}.");

        // Naming the build as the cause when bin/ already holds hundreds of assemblies is a wrong
        // diagnosis stated with confidence, and it sent one investigation down the wrong path.
        Console.WriteLine(graph.OutputReferences == 0
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