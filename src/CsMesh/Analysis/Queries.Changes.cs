using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// What changed in the shape of the codebase, as opposed to what changed in its text.
    ///
    /// git diff says a line moved. This says a binding disappeared. Those are not the same fact,
    /// and the gap between them is where a whole class of bug lives: move an AddScoped during a
    /// refactor, drop it by accident, and the solution still compiles, the unit tests still pass
    /// because they inject mocks, and the first container resolve at runtime throws. Nothing in
    /// the diff looks like anything but a moved line.
    ///
    /// Edges are matched on symbol names rather than node ids: ids are positional and every
    /// re-index reassigns them.
    /// </summary>
    public static int Changes(Graph current, Graph previous, bool includeCalls, BudgetWriter w, HashSet<string> dirty)
    {
        var now = Signatures(current, includeCalls);
        var before = Signatures(previous, includeCalls);

        var added = now.Keys.Except(before.Keys, StringComparer.Ordinal).ToList();
        var removed = before.Keys.Except(now.Keys, StringComparer.Ordinal).ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            w.Force("no structural change since the previous index.");
            w.Force(includeCalls
                ? "Nodes and edges are identical."
                : "Bindings, dispatches and implementations are identical. Add --calls to include call edges.");
            return Exit.Ok;
        }

        w.Force($"{removed.Count} edge(s) removed, {added.Count} added since the previous index");

        // Removals first, and deliberately: an edge that disappeared is the one that breaks
        // something. An edge that appeared is usually the feature being written.
        if (!Group("REMOVED", removed, before, "removed")) return Truncated(w);
        if (!Group("ADDED", added, now, "added")) return Truncated(w);

        var lostBindings = removed.Count(k => k.StartsWith("DiBinding", StringComparison.Ordinal));
        var lostDispatch = removed.Count(k => k.StartsWith("Mediatr", StringComparison.Ordinal));

        if (lostBindings > 0 || lostDispatch > 0)
        {
            w.Force("");
            w.Force($"WARNING  {lostBindings} binding(s) and {lostDispatch} dispatch(es) no longer resolve.");
            w.Force("         The compiler will not catch either. Check the registration site before merging.");
        }

        return Exit.Ok;

        bool Group(string title, List<string> keys, Dictionary<string, EdgeFact> facts, string relation)
        {
            if (keys.Count == 0) return true;
            if (!w.Add("")) return false;
            if (!w.Add(title)) return false;

            foreach (var kindGroup in keys
                         .Select(k => facts[k])
                         .GroupBy(f => f.Kind)
                         .OrderBy(x => StructuralRank(x.Key)))
            {
                if (!w.Add($"  {kindGroup.Key}  ({kindGroup.Count()})")) return false;

                foreach (var fact in kindGroup.Take(20))
                {
                    var note = fact.Note != null ? $"  [{fact.Note}]" : "";
                    var site = fact.Site != null ? $"  @ {fact.Site}" : "";

                    var row = new QueryRow
                    {
                        Depth = 1,
                        Symbol = $"{fact.From} -> {fact.To}",
                        Kind = fact.Kind.ToString(),
                        Relation = relation,
                        Note = fact.Note,
                        Site = fact.Site
                    };

                    if (!w.Add($"    {fact.From} -> {fact.To}{note}{site}", row)) return false;
                }

                if (kindGroup.Count() > 20 && !w.Add($"    ... {kindGroup.Count() - 20} more")) return false;
            }

            return true;
        }
    }

    /// <summary>Bindings and dispatch before inheritance, inheritance before plumbing.</summary>
    private static int StructuralRank(EdgeKind kind) => kind switch
    {
        EdgeKind.DiBinding => 0,
        EdgeKind.Mediatr => 1,
        EdgeKind.Route => 2,
        EdgeKind.Interface => 3,
        EdgeKind.Override => 4,
        _ => 5
    };

    private readonly record struct EdgeFact(string From, string To, EdgeKind Kind, string? Note, string? Site);

    /// <summary>
    /// Call edges are excluded by default. They churn on every refactor and would bury the handful
    /// of structural changes that actually alter behaviour.
    /// </summary>
    private static Dictionary<string, EdgeFact> Signatures(Graph g, bool includeCalls)
    {
        var map = new Dictionary<string, EdgeFact>(StringComparer.Ordinal);

        foreach (var e in g.Edges)
        {
            if (e.Kind == EdgeKind.TypeUse) continue;
            if (!includeCalls && e.Kind is EdgeKind.Call or EdgeKind.Construct) continue;

            var from = g.ById(e.From);
            var to = g.ById(e.To);
            if (from == null || to == null) continue;

            map[$"{e.Kind}|{from.Name}|{to.Name}"] = new EdgeFact(from.Short, to.Short, e.Kind, e.Note, e.Site);
        }

        return map;
    }
}
