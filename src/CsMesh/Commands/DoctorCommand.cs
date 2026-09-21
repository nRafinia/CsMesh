using System.Text.Json;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Skill;
using CsMesh.Storage;
using CsMesh.Telemetry;

namespace CsMesh.Commands;

public static class DoctorCommand
{
    public static int Execute(string root, Options opt) => Execute(root, opt, SkillCommand.GetHomeDir());

    internal static int Execute(string root, Options opt, string home)
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
            report.RazorFileCount = graph.RazorFileCount;
            report.RazorComponentsIndexed = graph.RazorComponentsIndexed;
            report.RazorStaleSources = graph.RazorStaleSources;
            report.GeneratedSourcesIndexed = graph.GeneratedSourcesIndexed;
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

            // Source the ownership rules left out is named rather than dropped in silence. Both are
            // printed only when non-zero so the common case stays quiet.
            if (graph.ExcludedLooseFiles > 0)
            {
                e.Line($"loose files     {graph.ExcludedLooseFiles} .cs file(s) outside every project are not indexed");
            }

            if (graph.UnevaluableCompileItems > 0)
            {
                e.Line($"unevaluable     {graph.UnevaluableCompileItems} compile item(s) name an MSBuild property not evaluated");
            }

            if (graph.UnevaluableInternalsVisibleTo > 0)
            {
                e.Line($"ivt             {graph.UnevaluableInternalsVisibleTo} InternalsVisibleTo item(s) name an MSBuild property not evaluated");
            }

            if (graph.UnevaluableUsings > 0)
            {
                e.Line($"usings          {graph.UnevaluableUsings} Using item(s) name an MSBuild property not evaluated");
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
            e.Line("skill (local)   NOT INSTALLED -> run: csmesh install");
        }

        var installedGlobal = SkillCommand.GlobalSkillTargets(home).Where(File.Exists).Distinct().ToList();
        e.Line(installedGlobal.Count > 0
            ? $"skill (global)  installed ({installedGlobal.Count} target(s) across user assistants)"
            : "skill (global)  NOT INSTALLED -> run: csmesh install --global");

        // A block that exists but was written by another build. A warning, not a fault: without a
        // version stamp the direction is unknown (an older block and a hand-edited one look the
        // same), so it says "differ" and names the remedy rather than claiming anything is old.
        foreach (var path in StaleInstalledBlocks(root, home))
        {
            report.StaleInstructions.Add(path);
            e.Line($"{path}: installed csmesh instructions differ from this build \u2192 csmesh install");
        }

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
    /// Installed blocks, one path per file, whose bytes differ from what this build would write.
    ///
    /// Repo-local paths are shown relative to the root and user-global ones in full, because a
    /// global block's home directory is not under the repository the line is printed beside. A file
    /// with no block is left out entirely -- "not installed" is a different state from stale, and
    /// naming it here would turn an absent rule file into a false drift warning.
    /// </summary>
    private static List<string> StaleInstalledBlocks(string root, string home)
    {
        var stale = new List<string>();

        foreach (var path in SkillCommand.BlockTargets(root, isGlobal: false))
        {
            if (InstalledBlockDiffers(path)) stale.Add(Path.GetRelativePath(root, path));
        }

        foreach (var path in SkillCommand.BlockTargets(home, isGlobal: true))
        {
            if (InstalledBlockDiffers(path)) stale.Add(path);
        }

        return stale;
    }

    /// <summary>
    /// True when <paramref name="path"/> exists, carries a complete csmesh block, and that block is
    /// not the text this build renders. Reads with <see cref="FileShare.ReadWrite"/> so an editor or
    /// an assistant holding the file open does not turn a readable file into a false negative;
    /// anything unreadable is treated as no block, since doctor must not fail over a skill file.
    /// </summary>
    private static bool InstalledBlockDiffers(string path)
    {
        if (!File.Exists(path)) return false;

        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var installed = SkillBlock.Extract(text);
        if (installed is null) return false;

        return !string.Equals(
            SkillBlock.Normalize(installed),
            SkillBlock.Normalize(SkillBlock.Render(SkillText.Rules)),
            StringComparison.Ordinal);
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
            // Floored, not rounded. {rate:F1} turned 99.97% into 100.0%, so a graph with two
            // unresolved call sites read as a clean hundred and the one number a reader trusts
            // said "nothing to see". 99.9% is ugly and true; 100.0% is reserved for exactly full.
            var permille = (int)(1000L * bound / graph.TotalCallSites);
            e.Line($"  calls resolved  {permille / 10.0:0.0}%  ({bound}/{graph.TotalCallSites})");

            // The two causes are different jobs: a missing candidate is usually a missing
            // reference, while an ambiguous overload means two in-scope symbols fit and the
            // compiler refused to choose. Both are unresolved now; naming them separately is what
            // keeps a reference problem from being mistaken for an overload problem.
            var noCandidate = graph.UnresolvedByReason.GetValueOrDefault("call/no-candidate-symbol");
            var ambiguous = graph.UnresolvedByReason.GetValueOrDefault("call/ambiguous-overload");
            if (noCandidate + ambiguous > 0)
            {
                e.Line($"  calls unresolved {noCandidate + ambiguous}  " +
                       $"({noCandidate} no candidate, {ambiguous} ambiguous overload)");
            }

            // Only name the build when the reference set actually looks unbuilt. Telling someone
            // to run dotnet build on a solution whose bin/ already holds 223 assemblies is a
            // wrong diagnosis stated confidently, which is worse than no diagnosis.
            if (permille < 900)
            {
                e.Line(graph.OutputReferences == 0
                    ? "                  low, and nothing was loaded from bin/ -> run 'dotnet build', then re-index"
                    : "                  low despite a populated bin/ -> run 'csmesh unresolved --kind call' to see what is missing");
            }
        }

        if (graph.Diagnostics.Count > 0)
        {
            e.Line("  compiler said");

            // Grouped by project: one project's missing reference and another's bad using are
            // different problems, and a flat list lets the first project crowd the rest out. Each
            // project shows its five largest groups.
            var byProject = graph.Diagnostics
                .GroupBy(d => d.Project, StringComparer.Ordinal)
                .OrderByDescending(g => g.Max(x => x.Count))
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var project in byProject)
            {
                if (project.Key.Length > 0) e.Line($"    {project.Key}");

                var shown = 0;
                foreach (var note in project.OrderByDescending(x => x.Count))
                {
                    if (shown++ >= 5) break;
                    e.Line($"    {note.Id} x{note.Count,-6} {note.Message}");
                }

                if (project.Any(d => d.Id == "CS0433"))
                {
                    e.Line("    CS0433 means a type arrived from two assemblies. bin/ probably holds a");
                    e.Line("    compiled copy of the source being indexed; that breaks resolution.");
                }
            }
        }

        if (graph.ProjectCycles.Count > 0)
        {
            e.Line($"  cycle broken    {graph.ProjectCycles.Count} ProjectReference cycle edge(s);");
            e.Line("                  the compilation for each broken edge does not exist:");
            foreach (var cycle in graph.ProjectCycles.Take(8))
            {
                e.Line($"    {cycle}");
            }
        }

        if (graph.RazorFileCount > 0)
        {
            if (graph.RazorComponentsIndexed > 0)
            {
                e.Line($"  razor           {graph.RazorComponentsIndexed} of {graph.RazorFileCount} .razor/.cshtml file(s)" +
                       " indexed from generated sources found on disk");
            }
            else if (graph.RazorStaleSources == 0)
            {
                e.Line($"  razor           {graph.RazorFileCount} .razor/.cshtml file(s) found; their generated types");
                e.Line("                  (components, parameters, @code members) are not in the graph, so a diagnostic");
                e.Line("                  naming their namespace -- CS0246 is typical -- describes a missing build");
                e.Line("                  output, not broken code.");
                e.Line("                  their generator output was not found on disk. A warm build does not write it");
                e.Line("                  even with the property below set -- it only runs the generator again when the");
                e.Line("                  build is not incremental. Run both flags, then force a full re-index -- a plain");
                e.Line("                  'csmesh index' will not notice, since obj/ changing is not by itself a reason");
                e.Line("                  to rebind anything this indexer tracks:");
                e.Line("                  dotnet build --no-incremental -p:EmitCompilerGeneratedFiles=true && csmesh index --full");
            }

            // Separate from the two cases above: this is neither "never built" nor "up to date" --
            // a build happened at some point, but a later .razor/.cshtml edit was never rebuilt.
            // Indexing that generated output would look current while quietly holding pre-edit
            // content, which is worse than reporting nothing for that file.
            if (graph.RazorStaleSources > 0)
            {
                e.Line($"  razor stale     {graph.RazorStaleSources} generated source(s) skipped: the .razor/.cshtml file");
                e.Line("                  is newer than what was last compiled from it. Rebuilding alone will not refresh");
                e.Line("                  this line, either -- the .razor file itself did not change, so a plain 'csmesh");
                e.Line("                  index' has nothing to notice. Force it:");
                e.Line("                  dotnet build --no-incremental -p:EmitCompilerGeneratedFiles=true && csmesh index --full");
            }
        }

        // Non-Razor generators (System.Text.Json, Regex, LibraryImport, LoggerMessage, ...). Their
        // output is read the same way now, but nothing here infers from source that a generator is
        // in use -- that walk is deliberately absent. So this reports what was found and stays
        // quiet when nothing was; the "built but the output is missing" warning for these needs a
        // cheap source-side signal, which is the GeneratorClaims walk, not a guess made here.
        if (graph.GeneratedSourcesIndexed > 0)
        {
            e.Line($"  generated       {graph.GeneratedSourcesIndexed} non-Razor generated .g.cs file(s) indexed" +
                   " from obj/**/generated");
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