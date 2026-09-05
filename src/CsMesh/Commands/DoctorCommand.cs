using System.Text.Json;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using CsMesh.Telemetry;

namespace CsMesh.Commands;

public static class DoctorCommand
{
    public static int Execute(string root, Options opt)
    {
        var e = new Emit(opt.Flag("json"));
        var report = new DoctorReport
        {
            Root = root,
            RunningVersion = AppVersion.Get()
        };

        e.Line($"repo            {root}");
        var graph = GraphStore.Load(root, out var problem);

        if (graph == null)
        {
            report.Problem = problem;
            e.Line($"index           {problem}  -> run: csmesh index");
        }
        else
        {
            report.HasIndex = true;
            report.Nodes = graph.Nodes.Count;
            report.Edges = graph.Edges.Count;
            report.BuiltByVersion = graph.BuiltByVersion;
            report.VersionGap = GraphStore.BuiltByOtherVersion(graph);
            report.FormatVersion = graph.FormatVersion;
            report.BuiltFromCommit = graph.BuiltFromCommit;
            report.BuiltAt = graph.BuiltAt;
            report.IncrementalRefreshes = graph.IncrementalRefreshes;
            report.ReferenceCount = graph.ReferenceCount;
            report.RuntimeReferences = graph.RuntimeReferences;
            report.OutputReferences = graph.OutputReferences;
            report.OutputDirectories = graph.OutputDirectories;
            report.ReferencesFailed = graph.ReferencesFailed;
            report.ReferencesCapped = graph.ReferencesCapped;
            report.UnresolvedCallSites = graph.UnresolvedCallSites;
            report.GlobalUsingSources = graph.GlobalUsingSources;
            report.SkippedProjects = graph.SkippedProjects;
            report.ScopeDecision = graph.ScopeDecision;
            report.EdgesByKind = graph.Edges
                .GroupBy(x => x.Kind.ToString())
                .ToDictionary(x => x.Key, x => x.Count());
            var dirty = GraphStore.DirtyFiles(graph);
            report.StaleFiles = dirty.Count;
            var age = DateTimeOffset.UtcNow - graph.BuiltAt;
            var commitDisplay = graph.BuiltFromCommit.Length > 0 ? graph.BuiltFromCommit : "n/a";

            e.Line($"index           {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, built {age.TotalHours:F1}h ago (commit {commitDisplay})");
            e.Line($"freshness       {(dirty.Count == 0 ? "clean" : $"{dirty.Count} file(s) changed since index -> answers will be marked [STALE]")}");

            // Freshness above only compares the graph against the working tree. It says nothing
            // about whether the rules that built it are the rules this binary would apply now.
            e.Line(GraphStore.BuiltByOtherVersion(graph)
                ? $"built by        {GraphStore.VersionGap(graph)} -> run: csmesh index --full"
                : $"built by        csmesh {AppVersion.Get()}");

            var resolution = graph.UnresolvedCallSites == 0
                ? $"clean ({graph.ReferenceCount} references)"
                : $"{graph.UnresolvedCallSites} unbound call site(s) against {graph.ReferenceCount} references";
            e.Line($"resolution      {resolution}");

            if (graph.ScopeDecision.Length > 0)
            {
                e.Line($"scope           {graph.ScopeDecision}");
            }

            if (graph.SkippedProjects.Count > 0)
            {
                e.Line($"skipped         {graph.SkippedProjects.Count} project(s): {graph.SkippedProjectsReason}");
                foreach (var project in graph.SkippedProjects.Take(10))
                {
                    e.Line($"  {project}");
                }

                e.Line("  their symbols, registrations and unresolved references are absent by design.");
                e.Line("  include them with: csmesh index --all");
            }

            e.Line($"global usings   {graph.GlobalUsingSources} set(s) compiled in"
                              + (graph.GlobalUsingSources == 0
                                  ? "  -- none; the System namespace is missing and nothing will bind"
                                  : ""));

            e.Line($"references      {graph.ReferenceCount} total"
                              + $" -- {graph.RuntimeReferences} runtime,"
                              + $" {graph.OutputReferences} from bin/"
                              + (graph.ReferencesCapped ? " (a directory scan hit its cap)" : "")
                              + (graph.ReferencesFailed > 0 ? $", {graph.ReferencesFailed} could not be opened" : ""));

            if (graph.ShadowedOutputs > 0)
            {
                e.Line($"                {graph.ShadowedOutputs} project output(s) excluded: those types come from source,");
                e.Line("                and referencing them as well makes every extension method call ambiguous.");
            }

            if (graph.OutputReferences == 0 && graph.UnresolvedCallSites > 0)
            {
                e.Line("                NOTHING FROM bin/. The solution was not built when this index was made,");
                e.Line("                so every package type is unbound and the graph is missing edges.");
                e.Line("                Run: dotnet build, then csmesh index");
            }
            else if (graph.UnresolvedCallSites > 0 && graph.OutputReferences < graph.OutputDirectories)
            {
                e.Line($"                {graph.OutputDirectories} bin/ director(ies) found but only {graph.OutputReferences} assembl(ies) loaded;");
                e.Line("                library projects may need CopyLocalLockFileAssemblies to emit package DLLs.");
            }

            foreach (var group in graph.Edges.GroupBy(e => e.Kind).OrderByDescending(x => x.Count()))
            {
                e.Line($"  edges {group.Key,-10} {group.Count()}");
            }

            if (graph.Edges.Count(e => e.Kind == EdgeKind.Mediatr) == 0)
            {
                e.Line("  note: no mediator edges found (applicable if MediatR/MassTransit is in use).");
            }

            if (graph.Edges.Count(e => e.Kind == EdgeKind.DiBinding) == 0)
            {
                e.Line("  note: no DI bindings resolved; interface implementations will not have di-bound ranking.");
            }

            if (graph.ScanRegistrations.Count > 0)
            {
                e.Line($"scanning        {graph.ScanRegistrations.Count} convention registration(s):");
                foreach (var entry in graph.ScanRegistrations.Take(8))
                {
                    e.Line($"  {entry}");
                }

                e.Line("  bindings from these are inferred, not named -- they carry a ?score in output.");
            }

            Quality(graph, e);
        }

        var installedLocal = SkillCommand.SkillTargets(root).Where(File.Exists).Distinct().ToList();
        if (installedLocal.Count > 0)
        {
            var relative = string.Join(", ", installedLocal.Select(p => Path.GetRelativePath(root, p)));
            e.Line($"skill (local)   installed ({relative})");
        }
        else
        {
            e.Line("skill (local)   NOT INSTALLED -> run: csmesh skill --install");
        }

        var home = SkillCommand.GetHomeDir();
        var installedGlobal = SkillCommand.GlobalSkillTargets(home).Where(File.Exists).Distinct().ToList();
        e.Line(installedGlobal.Count > 0
            ? $"skill (global)  installed ({installedGlobal.Count} target(s) across user assistants)"
            : "skill (global)  NOT INSTALLED -> run: csmesh skill --install --global");

        var (caller, via) = CallerDetector.Detect();
        e.Line($"caller now      {caller} (via {via}); tty={!Console.IsOutputRedirected}");
        e.Line($"parent chain    {CallerDetector.ParentChain() ?? "unavailable on this OS"}");

        var log = Telemetry.Telemetry.Read(root);
        var agent = log.Count(i => i.Caller != "human");
        e.Line($"telemetry       {log.Count} invocation(s) logged, {agent} agent-attributed -> {Telemetry.Telemetry.LogPath(root)}");

        if (log.Count > 0 && agent == 0)
        {
            e.Line("  warning: recorded invocations were attributed to human/direct terminal.");
        }

        report.Exit = Exit.Ok;
        report.Text.AddRange(e.Lines);

        if (e.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, AppJsonContext.Default.DoctorReport));
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
    private static void Quality(Graph graph, Emit e)
    {
        e.Line("graph quality");

        if (graph.TotalCallSites > 0)
        {
            var bound = graph.TotalCallSites - graph.UnresolvedCallSites;
            var rate = 100.0 * bound / graph.TotalCallSites;
            e.Line($"  calls resolved  {rate:F1}%  ({bound}/{graph.TotalCallSites})");
            // Only name the build when the reference set actually looks unbuilt. Telling someone
            // to run dotnet build on a solution whose bin/ already holds 223 assemblies is a
            // wrong diagnosis stated confidently, which is worse than no diagnosis.
            if (rate < 90)
            {
                e.Line(graph.OutputReferences == 0
                    ? "                  low, and nothing was loaded from bin/ -> run 'dotnet build', then re-index"
                    : "                  low despite a populated bin/ -> run 'csmesh unresolved --kind call' to see what is missing");
            }
        }

        if (graph.Diagnostics.Count > 0)
        {
            e.Line("  compiler said");
            foreach (var note in graph.Diagnostics.Take(5))
            {
                e.Line($"    {note.Id} x{note.Count,-6} {note.Message}");
            }

            if (graph.Diagnostics.Any(d => d.Id == "CS0433"))
            {
                e.Line("    CS0433 means a type arrived from two assemblies. bin/ probably holds a");
                e.Line("    compiled copy of the source being indexed; that breaks resolution.");
            }
        }

        if (graph.UnresolvedByReason.Count > 0)
        {
            e.Line("  unresolved by reason  (full counts, not the sample)");
            foreach (var (reason, count) in graph.UnresolvedByReason.OrderByDescending(x => x.Value).Take(6))
            {
                e.Line($"    {reason,-28} {count}");
            }
        }

        if (graph.UnresolvedByProject.Count > 1)
        {
            e.Line("  unresolved by project");
            foreach (var (project, count) in graph.UnresolvedByProject.OrderByDescending(x => x.Value).Take(6))
            {
                e.Line($"    {project,-28} {count}");
            }
        }

        var inferred = graph.Edges.Where(e => e.Confidence != null).ToList();
        var guesses = inferred.Where(e => e.Score < Edge.TrustThreshold).ToList();

        e.Line(inferred.Count == 0
            ? "  inferred edges  none; every edge came from a compiler symbol"
            : $"  inferred edges  {inferred.Count} of {graph.Edges.Count} were not read straight off a symbol");

        foreach (var group in inferred.GroupBy(e => e.Source ?? "unknown").OrderByDescending(x => x.Count()))
        {
            var worst = group.Min(e => e.Score);
            e.Line($"    {group.Key,-22} {group.Count(),4}  (lowest {worst:0.00})");
        }

        if (guesses.Count > 0)
        {
            e.Line($"  verify these    {guesses.Count} edge(s) below {Edge.TrustThreshold:0.00}; rows carry ?score in output");
        }

        if (graph.AmbiguousDiRegistrations > 0)
        {
            e.Line($"  ambiguous DI    {graph.AmbiguousDiRegistrations} registration(s) skipped: type name matched more than one type");
        }

        if (graph.AmbiguousMessageDispatches > 0)
        {
            e.Line($"  ambiguous msgs  {graph.AmbiguousMessageDispatches} Send/Publish site(s) skipped: request name matched more than one request type");
        }

        if (graph.UnmatchedMessageDispatches > 0)
        {
            e.Line($"  unhandled msgs  {graph.UnmatchedMessageDispatches} Send/Publish site(s) had no handler in this repository");
        }

        if (graph.Unresolved.Count > 0)
        {
            e.Line($"  locations       {graph.Unresolved.Count} sampled -> run: csmesh unresolved");
        }
        else if (graph.UnresolvedCallSites > 0)
        {
            e.Line("  locations       none recorded; this index predates them -> run: csmesh index");
        }
    }
}