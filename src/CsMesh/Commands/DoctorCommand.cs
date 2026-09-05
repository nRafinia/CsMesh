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
                : $"{graph.UnresolvedCallSites} unbound call site(s) against {graph.ReferenceCount} references";
            Console.WriteLine($"resolution      {resolution}");

            if (graph.ScopeDecision.Length > 0)
            {
                Console.WriteLine($"scope           {graph.ScopeDecision}");
            }

            if (graph.SkippedProjects.Count > 0)
            {
                Console.WriteLine($"skipped         {graph.SkippedProjects.Count} project(s): {graph.SkippedProjectsReason}");
                foreach (var project in graph.SkippedProjects.Take(10))
                {
                    Console.WriteLine($"  {project}");
                }

                Console.WriteLine("  their symbols, registrations and unresolved references are absent by design.");
                Console.WriteLine("  include them with: csmesh index --all");
            }

            Console.WriteLine($"global usings   {graph.GlobalUsingSources} set(s) compiled in"
                              + (graph.GlobalUsingSources == 0
                                  ? "  -- none; the System namespace is missing and nothing will bind"
                                  : ""));

            Console.WriteLine($"references      {graph.ReferenceCount} total"
                              + $" -- {graph.RuntimeReferences} runtime,"
                              + $" {graph.OutputReferences} from bin/"
                              + (graph.ReferencesCapped ? " (a directory scan hit its cap)" : "")
                              + (graph.ReferencesFailed > 0 ? $", {graph.ReferencesFailed} could not be opened" : ""));

            if (graph.ShadowedOutputs > 0)
            {
                Console.WriteLine($"                {graph.ShadowedOutputs} project output(s) excluded: those types come from source,");
                Console.WriteLine("                and referencing them as well makes every extension method call ambiguous.");
            }

            if (graph.OutputReferences == 0 && graph.UnresolvedCallSites > 0)
            {
                Console.WriteLine("                NOTHING FROM bin/. The solution was not built when this index was made,");
                Console.WriteLine("                so every package type is unbound and the graph is missing edges.");
                Console.WriteLine("                Run: dotnet build, then csmesh index");
            }
            else if (graph.UnresolvedCallSites > 0 && graph.OutputReferences < graph.OutputDirectories)
            {
                Console.WriteLine($"                {graph.OutputDirectories} bin/ director(ies) found but only {graph.OutputReferences} assembl(ies) loaded;");
                Console.WriteLine("                library projects may need CopyLocalLockFileAssemblies to emit package DLLs.");
            }

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

            if (graph.ScanRegistrations.Count > 0)
            {
                Console.WriteLine($"scanning        {graph.ScanRegistrations.Count} convention registration(s):");
                foreach (var entry in graph.ScanRegistrations.Take(8))
                {
                    Console.WriteLine($"  {entry}");
                }

                Console.WriteLine("  bindings from these are inferred, not named -- they carry a ?score in output.");
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
            // Only name the build when the reference set actually looks unbuilt. Telling someone
            // to run dotnet build on a solution whose bin/ already holds 223 assemblies is a
            // wrong diagnosis stated confidently, which is worse than no diagnosis.
            if (rate < 90)
            {
                Console.WriteLine(graph.OutputReferences == 0
                    ? "                  low, and nothing was loaded from bin/ -> run 'dotnet build', then re-index"
                    : "                  low despite a populated bin/ -> run 'csmesh unresolved --kind call' to see what is missing");
            }
        }

        if (graph.Diagnostics.Count > 0)
        {
            Console.WriteLine("  compiler said");
            foreach (var note in graph.Diagnostics.Take(5))
            {
                Console.WriteLine($"    {note.Id} x{note.Count,-6} {note.Message}");
            }

            if (graph.Diagnostics.Any(d => d.Id == "CS0433"))
            {
                Console.WriteLine("    CS0433 means a type arrived from two assemblies. bin/ probably holds a");
                Console.WriteLine("    compiled copy of the source being indexed; that breaks resolution.");
            }
        }

        if (graph.UnresolvedByReason.Count > 0)
        {
            Console.WriteLine("  unresolved by reason  (full counts, not the sample)");
            foreach (var (reason, count) in graph.UnresolvedByReason.OrderByDescending(x => x.Value).Take(6))
            {
                Console.WriteLine($"    {reason,-28} {count}");
            }
        }

        if (graph.UnresolvedByProject.Count > 1)
        {
            Console.WriteLine("  unresolved by project");
            foreach (var (project, count) in graph.UnresolvedByProject.OrderByDescending(x => x.Value).Take(6))
            {
                Console.WriteLine($"    {project,-28} {count}");
            }
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