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
        var graph = GraphStore.Load(root);

        if (graph == null)
        {
            Console.WriteLine("index           MISSING  -> run: csmesh index");
        }
        else
        {
            graph.Root = root;
            var dirty = GraphStore.DirtyFiles(graph);
            var age = DateTimeOffset.UtcNow - graph.BuiltAt;
            var commitDisplay = graph.BuiltFromCommit.Length > 0 ? graph.BuiltFromCommit : "n/a";

            Console.WriteLine($"index           {graph.Nodes.Count} nodes, {graph.Edges.Count} edges, built {age.TotalHours:F1}h ago (commit {commitDisplay})");
            Console.WriteLine($"freshness       {(dirty.Count == 0 ? "clean" : $"{dirty.Count} file(s) changed since index -> answers will be marked [STALE]")}");

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
        if (installedGlobal.Count > 0)
        {
            Console.WriteLine($"skill (global)  installed ({installedGlobal.Count} target(s) across user assistants)");
        }
        else
        {
            Console.WriteLine("skill (global)  NOT INSTALLED -> run: csmesh skill --install --global");
        }


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
}
