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
    /// Edges are matched on the compiler's symbol key. They used to be matched on display names,
    /// because node ids were positional and every re-index reassigned them -- but a display name
    /// drops parameter types, so `Handle(CreateOrder)` and `Handle(CancelOrder)` shared one entry
    /// and the second silently overwrote the first. On a MediatR codebase, where every handler is
    /// called Handle, that is most of the dispatch edges in the solution. Node.Key is persisted
    /// now, and it carries the container, arity and fully qualified parameter types.
    ///
    /// Three dimensions, not two. An edge can also stay in place and get worse: a binding the
    /// indexer read off a compiler symbol last time and can only guess at now is not an edge that
    /// changed, it is an edge that stopped being trustworthy, and it is almost always a reference
    /// that went missing. Reporting it as unchanged is the wrong answer.
    /// </summary>
    public static int Changes(Graph current, Graph previous, bool includeCalls, BudgetWriter w, HashSet<string> dirty)
    {
        var now = Signatures(current, includeCalls);
        var before = Signatures(previous, includeCalls);

        var added = now.Keys.Except(before.Keys, StringComparer.Ordinal).ToList();
        var removed = before.Keys.Except(now.Keys, StringComparer.Ordinal).ToList();

        // Confidence noise is not a change. A drop of a hundredth is float drift; a drop from 1.00
        // to 0.70 means the indexer went from reading a symbol to guessing at a name.
        var degraded = now.Keys
            .Intersect(before.Keys, StringComparer.Ordinal)
            .Where(k => before[k].Score - now[k].Score > 0.05)
            .OrderBy(k => now[k].Score)
            .ToList();

        if (added.Count == 0 && removed.Count == 0 && degraded.Count == 0)
        {
            w.Force("no structural change since the previous index.");
            w.Force(includeCalls
                ? "Nodes and edges are identical."
                : "Bindings, dispatches and implementations are identical. Add --calls to include call edges.");
            return Exit.Ok;
        }

        w.Force($"{removed.Count} edge(s) removed, {added.Count} added"
                 + (degraded.Count > 0 ? $", {degraded.Count} degraded" : "")
                 + " since the previous index");

        // Removals first, and deliberately: an edge that disappeared is the one that breaks
        // something. An edge that appeared is usually the feature being written.
        if (!Group("REMOVED", removed, before, "removed")) return Truncated(w);
        if (!Group("ADDED", added, now, "added")) return Truncated(w);

        if (degraded.Count > 0)
        {
            if (!w.Add("")) return Truncated(w);
            if (!w.Add("DEGRADED  the edge still exists; csmesh is less sure of it")) return Truncated(w);

            foreach (var key in degraded.Take(20))
            {
                var fact = now[key];
                var source = fact.Source != null ? $" {fact.Source}" : "";
                var line = $"    {fact.From} -> {fact.To}  [{fact.Kind}]  "
                           + $"{before[key].Score:0.00} -> {fact.Score:0.00}{source}";

                var row = new QueryRow
                {
                    Depth = 1,
                    Symbol = $"{fact.From} -> {fact.To}",
                    Kind = fact.Kind.ToString(),
                    Relation = "degraded",
                    Note = $"{before[key].Score:0.00} -> {fact.Score:0.00}",
                    Confidence = fact.Score,
                    Source = fact.Source,
                    Site = fact.Site
                };

                if (!w.Add(line, row)) return Truncated(w);
            }

            if (degraded.Count > 20 && !w.Add($"    ... {degraded.Count - 20} more")) return Truncated(w);
        }

        var lostBindings = removed.Count(k => k.StartsWith("DiBinding", StringComparison.Ordinal));
        var lostDispatch = removed.Count(k => k.StartsWith("Mediatr", StringComparison.Ordinal));

        if (lostBindings > 0 || lostDispatch > 0)
        {
            w.Force("");
            w.Force($"WARNING  {lostBindings} binding(s) and {lostDispatch} dispatch(es) no longer resolve.");
            w.Force("         The compiler will not catch either. Check the registration site before merging.");
        }

        // A lifetime is not decoration. Moving a registration from scoped to singleton keeps every
        // edge in place and changes when the object is built, which is where captive dependencies
        // and cross-request state leaks come from. It shows up here as one removed edge and one
        // added edge with a different note, and it is worth saying out loud.
        var lifetimeShifts = removed
            .Where(k => k.StartsWith("DiBinding", StringComparison.Ordinal))
            .Select(k => (Key: k, Pair: PairOf(k)))
            .Where(x => added.Any(a => a.StartsWith("DiBinding", StringComparison.Ordinal) && PairOf(a) == x.Pair))
            .ToList();

        foreach (var (key, _) in lifetimeShifts.Take(6))
        {
            var wasNote = before[key].Note ?? "?";
            var nowKey = added.First(a => a.StartsWith("DiBinding", StringComparison.Ordinal) && PairOf(a) == PairOf(key));
            var nowNote = now[nowKey].Note ?? "?";
            if (wasNote == nowNote) continue;

            w.Force("");
            w.Force($"LIFETIME {before[key].From} -> {before[key].To} changed from {wasNote} to {nowNote}.");
            w.Force("         Nothing in the diff or the compiler reports this.");
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

    private readonly record struct EdgeFact(
        string From, string To, EdgeKind Kind, string? Note, string? Site, double Score, string? Source);

    /// <summary>The two symbol keys of an edge signature, without its kind or note.</summary>
    private static string PairOf(string signature)
    {
        var first = signature.IndexOf('\u0001');
        if (first < 0) return signature;
        var second = signature.IndexOf('\u0001', first + 1);
        var third = second < 0 ? -1 : signature.IndexOf('\u0001', second + 1);
        return third < 0 ? signature[(first + 1)..] : signature[(first + 1)..third];
    }

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

            // Key, not Name, and the note is part of the identity: a binding that moved from
            // scoped to singleton is a different edge, and reporting it as unchanged hides the
            // only trace the change leaves anywhere outside the registration line itself.
            // A graph written before keys were persisted falls back to the display name.
            var fromKey = from.Key.Length > 0 ? from.Key : from.Name;
            var toKey = to.Key.Length > 0 ? to.Key : to.Name;

            // \u0001 as the separator: a fully qualified generic parameter list is full of
            // punctuation, and every printable delimiter tried appears inside real symbol keys.
            var signature = $"{e.Kind}\u0001{fromKey}\u0001{toKey}\u0001{e.Note}";

            map[signature] = new EdgeFact(
                from.Short, to.Short, e.Kind, e.Note, e.Site, e.Score, e.Source);
        }

        return map;
    }
}