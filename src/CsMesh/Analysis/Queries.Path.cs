using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// Answers "how does A eventually reach B". Trace only walks forward from one symbol and
    /// blast-radius only walks backward from one symbol; neither can connect two named symbols,
    /// which is the question an agent actually asks when it finds a class in a stack trace and
    /// wants to know why it is involved.
    ///
    /// Breadth-first, so the chain returned is the shortest one. Indirection counts as one hop
    /// like any other edge, which is the point: the interesting paths are the ones that go through
    /// a mediator dispatch or a container binding.
    /// </summary>
    public static int Path(Graph g, Node from, Node to, int depth, BudgetWriter w, HashSet<string> dirty)
    {
        var starts = Expand(g, from);
        var goals = Expand(g, to).Select(n => n.Id).ToHashSet();

        var previous = new Dictionary<int, (int From, Edge Via)>();
        var seen = starts.Select(n => n.Id).ToHashSet();
        var frontier = starts.ToList();
        var level = 0;
        int? landed = starts.FirstOrDefault(n => goals.Contains(n.Id))?.Id;

        while (landed == null && frontier.Count > 0 && level < depth)
        {
            var next = new List<Node>();
            foreach (var node in frontier)
            {
                foreach (var e in g.Out(node.Id).Where(IsFlow).OrderByDescending(e => e.Score))
                {
                    if (!seen.Add(e.To)) continue;
                    var target = g.ById(e.To);
                    if (target == null) continue;

                    previous[e.To] = (node.Id, e);
                    if (goals.Contains(e.To))
                    {
                        landed = e.To;
                        break;
                    }

                    next.Add(target);
                }

                if (landed != null) break;
            }

            frontier = next;
            level++;
        }

        if (landed == null)
        {
            w.Force($"no path: {from.Short} -> {to.Short} within depth {depth}.");
            w.Force($"For where the walk broke and why: csmesh silence {from.Short} {to.Short}");
            return Exit.NotFound;
        }

        // Walk the predecessor chain back to the seed, then emit it forwards.
        var chain = new List<(Node Node, Edge? Via)>();
        var cursor = landed.Value;
        while (true)
        {
            var node = g.ById(cursor);
            if (node == null) break;

            if (previous.TryGetValue(cursor, out var step))
            {
                chain.Add((node, step.Via));
                cursor = step.From;
                continue;
            }

            chain.Add((node, null));
            break;
        }

        chain.Reverse();

        var weakest = chain.Where(x => x.Via != null).Select(x => x.Via!.Score).DefaultIfEmpty(1.0).Min();
        var root = chain[0].Node;
        w.Force($"{root.Short}{TagSuffix(root)}{Loc(root)}{StaleTag(root, dirty)}",
                Row(root, 0, "root", null, dirty));

        var indent = "  ";
        for (var i = 1; i < chain.Count; i++)
        {
            var (node, via) = chain[i];
            var line = $"{indent}-> {node.Short}{Marker(via!)}{TagSuffix(node)}{Loc(node)}{StaleTag(node, dirty)}";
            if (!w.Add(line, Row(node, i, via!, dirty)))
            {
                w.Force("");
                w.Force($"OVER BUDGET: path is {chain.Count - 1} hop(s). Raise --budget.");
                return Exit.OverBudget;
            }

            indent += "  ";
        }

        if (weakest < Edge.TrustThreshold)
        {
            w.Force($"weakest link on this path: {weakest:0.00}. Open the file before relying on it.");
        }

        return Exit.Ok;
    }

    /// <summary>
    /// A query for a type should match anything that type owns. Asking how a controller reaches a
    /// repository should not fail because the edge happens to leave from a method of the
    /// controller rather than the controller itself.
    /// </summary>
    private static List<Node> Expand(Graph g, Node n)
    {
        var list = new List<Node> { n };
        if (n.Kind is not ("type" or "interface")) return list;

        foreach (var e in g.Out(n.Id).Where(x => x.Kind == EdgeKind.TypeUse))
        {
            if (g.ById(e.To) is { } member) list.Add(member);
        }

        return list;
    }
}
