using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// Lists where the indexer failed, grouped by reason.
    ///
    /// `doctor` says a graph is 91% resolved. That number is only actionable if you can see which
    /// 9%, because the failure mode of a structural tool is silence: a missing edge reads exactly
    /// like an absent one. Every row here is a place where an answer was thinner than it looked.
    /// </summary>
    public static int Unresolved(Graph g, string? kind, string? under, BudgetWriter w, HashSet<string> dirty)
    {
        var sites = g.Unresolved
            .Where(u => kind == null || string.Equals(u.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Where(u => under == null ||
                        u.File.Replace('\\', '/').StartsWith(under.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (g.Unresolved.Count == 0)
        {
            // Version 3 graphs and earlier carry the counters but not the locations.
            if (g.UnresolvedCallSites > 0 || g.AmbiguousDiRegistrations > 0 || g.AmbiguousMessageDispatches > 0)
            {
                w.Force($"{g.UnresolvedCallSites} unbound call site(s) counted, but this index has no locations.");
                w.Force("Re-run: csmesh index");
                return Exit.NoIndex;
            }

            w.Force("nothing unresolved: every edge the indexer looked for was found.");
            return Exit.Ok;
        }

        if (sites.Count == 0)
        {
            w.Force($"no unresolved sites of kind '{kind}'.");
            w.Force("kinds present: " + string.Join(", ", g.Unresolved.Select(u => u.Kind).Distinct().Order()));
            return Exit.NotFound;
        }

        w.Force($"{sites.Count} unresolved site(s) sampled");

        // Ordered by what is worth fixing, not by how many there are. A hundred unbound BCL calls
        // mean "build the solution"; two ambiguous DI registrations mean two services the graph
        // silently does not know about.
        foreach (var group in sites
                     .GroupBy(u => $"{u.Kind}/{u.Reason}")
                     .OrderBy(x => KindRank(x.First().Kind))
                     .ThenByDescending(x => x.Count()))
        {
            if (!w.Add("")) return TooMany(w, sites.Count);
            if (!w.Add($"{group.Key}  ({group.Count()})")) return TooMany(w, sites.Count);

            foreach (var site in group.Take(12))
            {
                var stale = site.File.Length > 0 && dirty.Contains(site.File) ? "  [STALE]" : "";
                var row = new QueryRow
                {
                    Depth = 1,
                    Symbol = site.Expression,
                    Kind = site.Kind,
                    Relation = "unresolved",
                    Note = site.Reason,
                    File = site.File.Length > 0 ? site.File : null,
                    Line = site.Line,
                    Stale = stale.Length > 0
                };

                if (!w.Add($"  {site.File}:{site.Line}  {site.Expression}{stale}", row))
                {
                    return TooMany(w, sites.Count);
                }
            }

            if (group.Count() > 12 && !w.Add($"  ... {group.Count() - 12} more"))
            {
                return TooMany(w, sites.Count);
            }
        }

        if (!w.Add("")) return Exit.Ok;
        w.Add(Advice(sites));

        return Exit.Ok;
    }

    private static int TooMany(BudgetWriter w, int total)
    {
        w.Force("");
        w.Force($"OVER BUDGET: {total} unresolved site(s). Narrow it with --kind di, or raise --budget.");
        return Exit.OverBudget;
    }

    private static int KindRank(string kind) => kind switch
    {
        "di" => 0,
        "mediatr" => 1,
        "type" => 2,
        _ => 3
    };

    /// <summary>
    /// Turns the dominant failure reason into the one thing worth doing about it. A list of
    /// problems with no next step is a list an agent will ignore.
    /// </summary>
    private static string Advice(List<UnresolvedSite> sites)
    {
        // The advice should follow the most actionable kind, for the same reason the groups do.
        var dominant = sites
            .OrderBy(u => KindRank(u.Kind))
            .GroupBy(u => u.Reason)
            .First().Key;

        return dominant switch
        {
            "no-candidate-symbol" =>
                "Most failures are unbound symbols: run 'dotnet build' so bin/ assemblies exist, then re-index.",
            "ambiguous-overload" =>
                "Most failures are overload ambiguity: those call edges point at the first candidate and may be wrong.",
            "ambiguous-type-name" =>
                "Most failures are same-named types across namespaces: those DI registrations produced no binding at all.",
            "ambiguous-request-name" =>
                "Most failures are same-named request types: those Send/Publish sites were skipped rather than misrouted.",
            "unbound-type" =>
                "Most failures are type names the compiler could not bind: the reference set is incomplete, "
                + "so run 'dotnet build' and re-index before trusting any answer from this graph.",
            "no-handler" =>
                "Most failures are dispatches with no handler in this repository: the handler may live in another solution.",
            _ => "Open the listed files to confirm; these edges are missing from every query."
        };
    }
}
