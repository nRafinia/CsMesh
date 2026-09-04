using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using CsMesh.Telemetry;

namespace CsMesh.Commands;

public static class DoctorCommand
{
    public static int Execute(string root, Options opt)
    {
        Console.WriteLine($"repo            {root}");
        var graph = GraphStore.Load(root, out var problem);

        if (graph == null)
        {
            Console.WriteLine($"index           {problem}  -> run: csmesh index");
        }
        else
        {
            var dirty = GraphStore.DirtyFiles(graph);
            var age = DateTimeOffset.UtcNow - graph.BuiltAt;
            var commitDisplay = graph.BuiltFromCommit.Length > 0 ? graph.BuiltFromCommit : "n/a";

            Console.WriteLine($"index           {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, built {age.TotalHours:F1}h ago (commit {commitDisplay})");
            Console.WriteLine($"freshness       {(dirty.Count == 0 ? "clean" : $"{dirty.Count} file(s) changed since index -> answers will be marked [STALE]")}");

            var resolution = graph.UnresolvedCallSites == 0
                ? $"clean ({graph.ReferenceCount} references)"
                : $"{graph.UnresolvedCallSites} unbound call site(s) against {graph.ReferenceCount} references -> edges are missing, run 'dotnet build' then 'csmesh index'";
            Console.WriteLine($"resolution      {resolution}");

            foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()))
            {
                Console.WriteLine($"  edges {group.Key,-10} {group.Count()}");
            }

            if (graph.Edges.Count(e => e.Kind == EdgeKind.Mediatr) == 0)
            {
                Console.WriteLine("  note: no mediator edges found (applicable if MediatR/MassTransit is in use).");
            }

            if (graph.Edges.Count(e => e.Kind == EdgeKind.DiBinding) == 0)
            {
                Console.WriteLine("  note: no DI bindings resolved; interface implementations will not have di-bound ranking.");
            }

            Quality(graph);
        }

        var installedLocal = SkillCommand.SkillTargets(root).Where(File.Exists).Distinct().ToList();
        if (installedLocal.Count > 0)
        {
            var relative = string.Join(", ", installedLocal.Select(p => Path.GetRelativePath(root, p)));
            Console.WriteLine($"skill (local)   installed ({relative})");
        }
        else
        {
            Console.WriteLine("skill (local)   NOT INSTALLED -> run: csmesh skill --install");
        }

        var home = SkillCommand.GetHomeDir();
        var installedGlobal = SkillCommand.GlobalSkillTargets(home).Where(File.Exists).Distinct().ToList();
        Console.WriteLine(installedGlobal.Count > 0
            ? $"skill (global)  installed ({installedGlobal.Count} target(s) across user assistants)"
            : "skill (global)  NOT INSTALLED -> run: csmesh skill --install --global");

        var (caller, via) = CallerDetector.Detect();
        Console.WriteLine($"caller now      {caller} (via {via}); tty={!Console.IsOutputRedirected}");
        Console.WriteLine($"parent chain    {CallerDetector.ParentChain() ?? "unavailable on this OS"}");

        var log = Telemetry.Telemetry.Read(root);
        var agent = log.Count(i => i.Caller != "human");
        Console.WriteLine($"telemetry       {log.Count} invocation(s) logged, {agent} agent-attributed -> {Telemetry.Telemetry.LogPath(root)}");

        if (log.Count > 0 && agent == 0)
        {
            Console.WriteLine("  warning: recorded invocations were attributed to human/direct terminal.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// What the graph does not know, stated plainly.
    ///
    /// A structural tool that answers confidently from an 88%-resolved graph is worse than one
    /// that answers and says 88%, because silence reads as "there is nothing there" rather than
    /// "I could not see it". Everything here is a count of edges that were wanted and not made.
    /// </summary>
    private static void Quality(Graph graph)
    {
        Console.WriteLine("graph quality");

        if (graph.TotalCallSites > 0)
        {
            var bound = graph.TotalCallSites - graph.UnresolvedCallSites;
            var rate = 100.0 * bound / graph.TotalCallSites;
            Console.WriteLine($"  calls resolved  {rate:F1}%  ({bound}/{graph.TotalCallSites})");
            if (rate < 90) Console.WriteLine("                  low -> run 'dotnet build' so bin/ assemblies are available, then re-index");
        }

        var inferred = graph.Edges.Where(e => e.Confidence != null).ToList();
        var guesses = inferred.Where(e => e.Score < Edge.TrustThreshold).ToList();

        Console.WriteLine(inferred.Count == 0
            ? "  inferred edges  none; every edge came from a compiler symbol"
            : $"  inferred edges  {inferred.Count} of {graph.Edges.Count} were not read straight off a symbol");

        foreach (var group in inferred.GroupBy(e => e.Source ?? "unknown").OrderByDescending(x => x.Count()))
        {
            var worst = group.Min(e => e.Score);
            Console.WriteLine($"    {group.Key,-22} {group.Count(),4}  (lowest {worst:0.00})");
        }

        if (guesses.Count > 0)
        {
            Console.WriteLine($"  verify these    {guesses.Count} edge(s) below {Edge.TrustThreshold:0.00}; rows carry ?score in output");
        }

        if (graph.AmbiguousDiRegistrations > 0)
        {
            Console.WriteLine($"  ambiguous DI    {graph.AmbiguousDiRegistrations} registration(s) skipped: type name matched more than one type");
        }

        if (graph.AmbiguousMessageDispatches > 0)
        {
            Console.WriteLine($"  ambiguous msgs  {graph.AmbiguousMessageDispatches} Send/Publish site(s) skipped: request name matched more than one request type");
        }

        if (graph.UnmatchedMessageDispatches > 0)
        {
            Console.WriteLine($"  unhandled msgs  {graph.UnmatchedMessageDispatches} Send/Publish site(s) had no handler in this repository");
        }

        if (graph.Unresolved.Count > 0)
        {
            Console.WriteLine($"  locations       {graph.Unresolved.Count} sampled -> run: csmesh unresolved");
        }
        else if (graph.UnresolvedCallSites > 0)
        {
            Console.WriteLine("  locations       none recorded; this index predates them -> run: csmesh index");
        }
    }
}
