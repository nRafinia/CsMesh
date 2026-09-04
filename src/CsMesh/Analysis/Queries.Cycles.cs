using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// Finds circular dependencies by collapsing the member-level graph to whichever level was
    /// asked for and running Tarjan on the result. A cycle between two methods of the same class
    /// is ordinary recursion; a cycle between two classes, or two namespaces, is a design problem,
    /// which is why nothing below type level is reported.
    /// </summary>
    public static int Cycles(Graph g, string scope, BudgetWriter w, HashSet<string> dirty)
    {
        var groupOf = new Dictionary<int, string>();
        var sample = new Dictionary<string, Node>(StringComparer.Ordinal);

        foreach (var node in g.Nodes)
        {
            var owner = OwnerType(g, node);
            if (owner == null) continue;

            var group = scope == "namespace" ? Namespace(owner) : owner.Short;
            if (group.Length == 0) continue;

            groupOf[node.Id] = group;
            sample.TryAdd(group, owner);
        }

        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var e in g.Edges)
        {
            if (!IsFlow(e)) continue;
            if (!groupOf.TryGetValue(e.From, out var a) || !groupOf.TryGetValue(e.To, out var b)) continue;
            if (a == b) continue;

            if (!adjacency.TryGetValue(a, out var set)) adjacency[a] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(b);
        }

        var components = StronglyConnected(adjacency).Where(c => c.Count > 1).ToList();

        if (components.Count == 0)
        {
            w.Force($"no circular dependencies at {scope} level.");
            return Exit.Ok;
        }

        w.Force($"{components.Count} circular dependency group(s) at {scope} level");

        var index = 0;
        foreach (var component in components.OrderByDescending(c => c.Count))
        {
            index++;
            var cycle = ShortestCycle(adjacency, component);

            if (!w.Add("")) return Truncate(w, components.Count);
            if (!w.Add($"cycle #{index}  shortest loop of a {component.Count}-member group"))
                return Truncate(w, components.Count);

            foreach (var member in cycle)
            {
                var node = sample.GetValueOrDefault(member);
                var location = node != null ? Loc(node) : "";
                var stale = node != null ? StaleTag(node, dirty) : "";
                var row = node != null ? Row(node, 1, "cycle", $"#{index}", dirty) : null;

                if (!w.Add($"  -> {member}{location}{stale}", row)) return Truncate(w, components.Count);
            }

            var rest = component.Where(m => !cycle.Contains(m)).Take(8).ToList();
            if (rest.Count > 0 && !w.Add("  also entangled: " + string.Join(", ", rest)))
            {
                return Truncate(w, components.Count);
            }
        }

        return Exit.Ok;
    }

    private static int Truncate(BudgetWriter w, int total)
    {
        w.Force("");
        w.Force($"OVER BUDGET: {total} cycle group(s). Raise --budget, or use --namespace for a coarser view.");
        return Exit.OverBudget;
    }

    /// <summary>The type a node belongs to, or the node itself when it is already a type.</summary>
    private static Node? OwnerType(Graph g, Node n)
    {
        if (n.Kind is "type" or "interface") return n;

        foreach (var e in g.In(n.Id))
        {
            if (e.Kind != EdgeKind.TypeUse) continue;
            var owner = g.ById(e.From);
            if (owner is { Kind: "type" or "interface" }) return owner;
        }

        return null;
    }

    private static string Namespace(Node type)
    {
        var dot = type.Name.LastIndexOf('.');
        return dot <= 0 ? "" : type.Name[..dot];
    }

    /// <summary>
    /// Tarjan, iterative. A recursive version stack-overflows on a large solution, which is the
    /// only reason this is written out longhand.
    /// </summary>
    private static List<List<string>> StronglyConnected(Dictionary<string, HashSet<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var low = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var result = new List<List<string>>();
        var counter = 0;

        foreach (var root in adjacency.Keys)
        {
            if (index.ContainsKey(root)) continue;

            var work = new Stack<(string Node, IEnumerator<string> Edges)>();
            index[root] = low[root] = counter++;
            stack.Push(root);
            onStack.Add(root);
            work.Push((root, adjacency[root].GetEnumerator()));

            while (work.Count > 0)
            {
                var (node, edges) = work.Peek();

                if (edges.MoveNext())
                {
                    var next = edges.Current;
                    if (!index.ContainsKey(next))
                    {
                        index[next] = low[next] = counter++;
                        stack.Push(next);
                        onStack.Add(next);
                        work.Push((next, adjacency.GetValueOrDefault(next, []).GetEnumerator()));
                    }
                    else if (onStack.Contains(next))
                    {
                        low[node] = Math.Min(low[node], index[next]);
                    }

                    continue;
                }

                work.Pop();

                if (low[node] == index[node])
                {
                    var component = new List<string>();
                    string member;
                    do
                    {
                        member = stack.Pop();
                        onStack.Remove(member);
                        component.Add(member);
                    } while (member != node);

                    result.Add(component);
                }

                if (work.Count > 0)
                {
                    var parent = work.Peek().Node;
                    low[parent] = Math.Min(low[parent], low[node]);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Picks one concrete loop out of a component so the output shows a route rather than an
    /// unordered set. Breadth-first from an arbitrary member back to itself.
    /// </summary>
    private static List<string> ShortestCycle(Dictionary<string, HashSet<string>> adjacency, List<string> component)
    {
        var inside = component.ToHashSet(StringComparer.Ordinal);
        var start = component[0];
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        var frontier = new List<string> { start };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (frontier.Count > 0)
        {
            var next = new List<string>();
            foreach (var node in frontier)
            {
                foreach (var edge in adjacency.GetValueOrDefault(node, []))
                {
                    if (!inside.Contains(edge)) continue;

                    if (edge == start)
                    {
                        var path = new List<string>();
                        var cursor = node;
                        while (cursor != start)
                        {
                            path.Add(cursor);
                            cursor = previous[cursor];
                        }

                        path.Add(start);
                        path.Reverse();
                        path.Add(start);
                        return path;
                    }

                    if (!seen.Add(edge)) continue;
                    previous[edge] = node;
                    next.Add(edge);
                }
            }

            frontier = next;
        }

        return component;
    }
}
