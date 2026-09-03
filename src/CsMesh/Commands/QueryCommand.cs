using CsMesh.Analysis;
using CsMesh.Common;
using CsMesh.Storage;

namespace CsMesh.Commands;

public static class QueryCommand
{
    public static int Execute(string root, Options opt, string kind)
    {
        var graph = GraphStore.Load(root);
        if (graph == null)
        {
            Console.Error.WriteLine("no index. run: csmesh index");
            return Exit.NoIndex;
        }

        graph.Root = root;

        var dirty = GraphStore.DirtyFiles(graph);
        Dbg.Log($"index built {graph.BuiltAt:u} commit={graph.BuiltFromCommit} dirty={dirty.Count}");

        var budget = opt.Int("budget", 600);
        var depth = opt.Int("depth", kind == "blast" ? 3 : 6);
        var writer = new BudgetWriter(budget);

        if (dirty.Count > 0)
        {
            writer.Force($"# index is {dirty.Count} file(s) behind working tree; rows from those files are marked [STALE]. run: csmesh index");
        }

        int exitCode;
        if (kind == "entrypoints")
        {
            exitCode = Queries.Entrypoints(graph, opt.Positional.FirstOrDefault(), writer);
        }
        else
        {
            var query = opt.Positional.FirstOrDefault();
            if (query == null)
            {
                Console.Error.WriteLine($"usage: csmesh {kind} <symbol>");
                return Exit.Usage;
            }

            var candidates = graph.Resolve(query);
            if (candidates.Count == 0)
            {
                Console.WriteLine($"not found: {query}");
                var near = graph.Nodes
                    .Where(n => n.Short.Contains(query.Split('.').Last(), StringComparison.OrdinalIgnoreCase))
                    .Take(5)
                    .Select(n => n.Short)
                    .ToList();

                if (near.Count > 0)
                {
                    Console.WriteLine("did you mean: " + string.Join(", ", near));
                }
                return Exit.NotFound;
            }

            var wanted = kind == "impl"
                ? candidates.Where(c => c.Kind is "interface" or "type").ToList()
                : candidates;

            if (wanted.Count == 0) wanted = candidates;

            if (wanted.Count > 1)
            {
                Console.WriteLine($"ambiguous: {wanted.Count} matches for '{query}'");
                foreach (var candidate in wanted.Take(12))
                {
                    Console.WriteLine($"  {candidate.Name}  ({candidate.Kind})  {candidate.File}:{candidate.Line}");
                }
                return Exit.Ambiguous;
            }

            var node = wanted[0];
            var dirtySet = dirty.ToHashSet(StringComparer.OrdinalIgnoreCase);

            exitCode = kind switch
            {
                "trace" => Queries.Trace(graph, node, depth, writer, dirtySet),
                "impl" => Queries.Impl(graph, node, writer, dirtySet),
                "blast" => Queries.BlastRadius(graph, node, depth, writer, dirtySet),
                _ => Exit.Usage
            };

            Telemetry.Telemetry.Current.FilesAvoided = writer.Lines
                .Count(l => l.Contains(".cs:", StringComparison.Ordinal));
        }

        writer.Flush();
        Dbg.Log($"emitted {writer.Tokens} tokens (budget {budget}), exit {exitCode}");
        return exitCode;
    }
}
