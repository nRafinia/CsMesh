using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// The command every other command assumed had already been run.
    ///
    /// trace, impl, blast-radius and context all take a symbol. An agent handed "add discount
    /// codes to checkout" has no symbol, so its first move is grep -- which is exactly the round
    /// trip this tool exists to remove. The gap was not in the graph; it was in the entry.
    ///
    /// The mechanism was already here, mislabelled as an error: Graph.Resolve falls back to a
    /// substring match, and when that returns more than one node the caller gets exit 3 and a flat
    /// alphabetical list. Same search, three things missing -- file paths and route templates were
    /// never searched, nothing was ranked, and a successful search reported itself as ambiguity.
    ///
    /// Ranking is where grep cannot follow. A name match tells you a symbol exists; how many
    /// entrypoints reach it tells you whether it is the one the task is about. A DTO and the
    /// service method that fills it both match "discount"; only one of them is reached by three
    /// routes.
    /// </summary>
    public static int Where(Graph g, string[] terms, string? under, BudgetWriter w, HashSet<string> dirty)
    {
        var query = string.Join(" ", terms);
        var scope = string.IsNullOrWhiteSpace(under) ? null : under.Replace('\\', '/').Trim('/');

        var candidates = new List<Candidate>();

        foreach (var node in g.Nodes)
        {
            // Enum members match their parent's name constantly and are never the answer to
            // "where does this live".
            if (node.Kind == "enum-member") continue;
            if (scope != null && !WhereWithin(node.File, scope)) continue;

            var best = 0;
            var matched = 0;
            var why = "match";

            foreach (var term in terms)
            {
                var (points, reason) = WhereLexical(node, term);
                if (points == 0) continue;
                matched++;
                if (points <= best) continue;
                best = points;
                why = reason;
            }

            if (matched == 0) continue;

            // Every word hitting the same symbol is a much stronger signal than one word hitting
            // it hard, and a two-word query is usually two halves of one concept.
            if (terms.Length > 1 && matched == terms.Length) best += 25;

            candidates.Add(new Candidate(node, best, why));
        }

        if (candidates.Count == 0)
        {
            w.Force($"no symbol matches '{query}'.");
            w.Force("Names, namespaces, file paths and route templates were all searched.");
            w.Force("String literals, config values, TODOs and non-.cs files are not in the graph.");
            w.Force("For those, grep is the right tool. For a symbol you expected here: csmesh unresolved");
            return Exit.NotFound;
        }

        // Reach is measured per candidate, so it is measured on a shortlist. A bare term in a large
        // solution can match hundreds of names and walking backwards from all of them would cost
        // more than the search saves.
        var ranked = candidates
            .OrderByDescending(c => c.Lexical)
            .ThenBy(c => c.Node.Short.Length)
            .Take(WhereCandidateCap)
            .Select(c => WhereWeigh(g, c))
            .OrderByDescending(c => c.Score)
            .ToList();

        w.Force($"{query} -- {candidates.Count} match(es), ranked by what reaches them",
                Row(ranked[0].Node, 0, "query", query, dirty));

        var shown = 0;
        foreach (var c in ranked.Take(WhereRowCap))
        {
            var reach = c.Entrypoints == 0 && c.Callers == 0
                ? "  <- nothing reaches it"
                : $"  <- {c.Entrypoints} entrypoint(s), {c.Callers} caller(s)";

            var row = Row(c.Node, 1, "match", c.Why, dirty);
            row.Source = c.Why;

            var line = $"  {c.Node.Short}{TagSuffix(c.Node)}{Loc(c.Node)}{reach}  [{c.Why}]{StaleTag(c.Node, dirty)}";

            if (!w.Add(line, row))
            {
                w.Force("");
                w.Force($"OVER BUDGET after {shown} of {ranked.Count} match(es).");
                w.Force("Narrow with --under before raising --budget.");
                return Exit.OverBudget;
            }

            shown++;
        }

        if (candidates.Count > shown)
        {
            w.Add($"  ... {candidates.Count - shown} weaker match(es) not shown");
        }

        // A ranked list that does not say what to do with the winner has handed the round trip
        // straight back to the caller.
        var top = ranked[0];
        w.Force("");
        w.Force(top.Entrypoints > 0 || IsEntrypoint(top.Node)
            ? $"next: csmesh trace {top.Node.Short} --budget 600"
            : $"next: csmesh context {top.Node.Short} --budget 800");

        return Exit.Ok;
    }

    private const int WhereCandidateCap = 80;
    private const int WhereRowCap = 8;
    private const int WhereReachDepth = 4;
    private const int WhereReachCap = 600;

    private sealed class Candidate(Node node, int lexical, string why)
    {
        public Node Node { get; } = node;
        public int Lexical { get; } = lexical;
        public string Why { get; } = why;
        public int Callers { get; set; }
        public int Entrypoints { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// How well a single term matches a node, and on which axis. The axis is printed, because
    /// "matched the file path" and "matched the method name" are different levels of evidence and
    /// the caller should not have to guess which one produced the row.
    /// </summary>
    private static (int Points, string Why) WhereLexical(Node n, string term)
    {
        var leaf = WhereLeaf(n.Short);

        if (string.Equals(leaf, term, StringComparison.OrdinalIgnoreCase)) return (100, "name");
        if (string.Equals(n.Short, term, StringComparison.OrdinalIgnoreCase)) return (95, "name");

        // A route template is the closest thing in the graph to the words a task is written in.
        // "checkout" appearing in POST /checkout/discount outranks it appearing in a class name.
        foreach (var tag in n.Tags)
        {
            var routeish = tag.StartsWith("route:", StringComparison.Ordinal)
                           || tag.StartsWith("http:", StringComparison.Ordinal);
            if (routeish && tag.Contains(term, StringComparison.OrdinalIgnoreCase)) return (78, "route");
        }

        if (leaf.Contains(term, StringComparison.OrdinalIgnoreCase)) return (62, "name");
        if (n.Short.Contains(term, StringComparison.OrdinalIgnoreCase)) return (52, "name");
        if (n.Name.Contains(term, StringComparison.OrdinalIgnoreCase)) return (40, "namespace");
        if (n.File.Contains(term, StringComparison.OrdinalIgnoreCase)) return (28, "path");
        if (n.Signature.Contains(term, StringComparison.OrdinalIgnoreCase)) return (16, "signature");

        return (0, "");
    }

    /// <summary>
    /// Walks backwards from a candidate to find out whether anything runs it.
    ///
    /// This is the only part of the ranking text search cannot imitate, and it is what decides the
    /// order: a request DTO and the handler that consumes it match the same word, and the handler
    /// is the one with entrypoints above it.
    /// </summary>
    private static Candidate WhereWeigh(Graph g, Candidate c)
    {
        var entrypoints = new HashSet<int>();
        var seen = new HashSet<int> { c.Node.Id };
        var frontier = new List<int> { c.Node.Id };
        var visited = 0;

        for (var level = 0; level < WhereReachDepth && frontier.Count > 0 && visited < WhereReachCap; level++)
        {
            var next = new List<int>();

            foreach (var id in frontier)
            {
                foreach (var e in g.In(id))
                {
                    // TypeUse is ownership, not execution. A member is not "called" by its type.
                    if (e.Kind == EdgeKind.TypeUse) continue;
                    if (level == 0) c.Callers++;
                    if (!seen.Add(e.From)) continue;

                    visited++;
                    if (g.ById(e.From) is not { } from) continue;
                    if (IsEntrypoint(from)) entrypoints.Add(from.Id);
                    next.Add(from.Id);
                    if (visited >= WhereReachCap) break;
                }
            }

            frontier = next;
        }

        c.Entrypoints = entrypoints.Count;

        var wired = c.Node.Tags.Any(t => t.StartsWith("di:", StringComparison.Ordinal))
                    || c.Node.Tags.Contains("handler")
                    || c.Node.Tags.Contains("consumer")
                    || c.Node.Tags.Contains("hosted");

        var routed = c.Node.Tags.Any(t => t.StartsWith("route:", StringComparison.Ordinal)
                                          || t.StartsWith("http:", StringComparison.Ordinal));

        var score = (double)c.Lexical
                    + Math.Min(c.Entrypoints, 8) * 9
                    + Math.Min(c.Callers, 10) * 3
                    + (wired ? 12 : 0)
                    + (routed ? 14 : 0)
                    // A member is somewhere to start reading; a type is somewhere to start looking.
                    + (c.Node.Kind is "method" or "property" ? 6 : 0);

        // Test code matches product vocabulary as often as product code does and is almost never
        // the place a task starts. Demoted rather than dropped: sometimes the test is the answer.
        if (IsTest(c.Node)) score *= 0.35;
        if (c.Node.File.Length == 0) score *= 0.5;

        c.Score = score;
        return c;
    }

    private static string WhereLeaf(string s)
    {
        var dot = s.LastIndexOf('.');
        return dot < 0 ? s : s[(dot + 1)..];
    }

    private static bool WhereWithin(string file, string scope) =>
        file.Length > 0 &&
        file.Replace('\\', '/').StartsWith(scope, StringComparison.OrdinalIgnoreCase);
}

// x