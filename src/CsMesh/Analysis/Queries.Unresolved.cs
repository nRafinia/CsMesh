using CsMesh.Common;
using CsMesh.Models;

namespace CsMesh.Analysis;

public static partial class Queries
{
    /// <summary>
    /// How many groups and rows the sample prints before it stops and names what it withheld.
    /// The list is already a sample by construction -- the indexer caps it -- so this is the
    /// display bound, not a second cap on the data. Sized so the whole answer fits the 700-token
    /// default: a bound that does not fit the budget is not a bound, it is a slower overflow.
    /// </summary>
    private const int UnresolvedGroups = 4;
    private const int UnresolvedRowsPerGroup = 6;
    private const int UnresolvedReasonRows = 5;

    /// <summary>
    /// Lists where the indexer failed, grouped by reason.
    ///
    /// `doctor` says a graph is 91% resolved. That number is only actionable if you can see which
    /// 9%, because the failure mode of a structural tool is silence: a missing edge reads exactly
    /// like an absent one. Every row here is a place where an answer was thinner than it looked.
    ///
    /// The groups and rows are bounded, so a solution with thousands of unbound sites still answers
    /// in one screen. A capped answer is that sample by design and exits 0, with a footer naming how
    /// many groups and rows were withheld and the filter that narrows to them -- the same shape map
    /// uses for its per-section caps. The incomplete marker and exit 2 are reserved for the budget
    /// actually running out before the caps are reached, which is the different case of an answer
    /// that is genuinely truncated.
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

        // The index keeps only a bounded sample of locations per kind while the reason counts are
        // complete. Saying "N shown" for the bigger of the two read as "these are all of them" when
        // the displayed rows are a subset of the sample on top of that -- so the header names the
        // locations as a sample and lets the footer account for the rows actually printed.
        var total = kind == null
            ? g.UnresolvedByReason.Values.Sum()
            : g.UnresolvedByReason.Where(x => x.Key.StartsWith(kind + "/", StringComparison.Ordinal)).Sum(x => x.Value);
        w.Force(total > sites.Count
            ? $"{total} unresolved site(s) in total; {sites.Count} with locations in the sample"
            : $"{sites.Count} unresolved site(s)");

        // The sample is capped in traversal order, so its proportions are about the first files
        // walked. The full counts are the ones to reason from.
        if (g.UnresolvedByReason.Count > 0 && kind == null)
        {
            foreach (var (reason, count) in g.UnresolvedByReason.OrderByDescending(x => x.Value).Take(UnresolvedReasonRows))
            {
                if (!w.Add($"  {reason,-28} {count}")) return TooMany(w, total);
            }
        }

        // Ordered by what is worth fixing, not by how many there are. A hundred unbound BCL calls
        // mean "build the solution"; two ambiguous DI registrations mean two services the graph
        // silently does not know about.
        var groups = sites
            .GroupBy(u => $"{u.Kind}/{u.Reason}")
            .OrderBy(x => KindRank(x.First().Kind))
            .ThenByDescending(x => x.Count())
            .ToList();

        // Groups the caps withheld. A group with rows in it but not all of them counts here too, so
        // the footer's row total is the number of sites a reader did not see, not just the ones in
        // groups skipped whole.
        var capped = new List<(string Group, int Shown, int Total)>();
        var groupsShown = 0;

        foreach (var group in groups)
        {
            if (groupsShown >= UnresolvedGroups)
            {
                capped.Add((group.Key, 0, group.Count()));
                continue;
            }

            if (!w.Add("")) return TooMany(w, sites.Count);
            if (!w.Add($"{group.Key}  ({group.Count()})")) return TooMany(w, sites.Count);

            var shown = 0;
            foreach (var site in group.Take(UnresolvedRowsPerGroup))
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

                shown++;
            }

            if (group.Count() > shown) capped.Add((group.Key, shown, group.Count()));
            groupsShown++;
        }

        if (!w.Add("")) return Exit.Ok;
        w.Add(Advice(sites));

        if (capped.Count > 0)
        {
            // The group header already prints the group's full count, so the footer only has to say
            // the list is a sample and name the filter -- the reader can see which group was cut.
            var groupsWithheld = capped.Count(x => x.Shown == 0);
            var rowsWithheld = capped.Sum(x => x.Total - x.Shown);
            var withheld = groupsWithheld > 0
                ? $"{groupsWithheld} group(s) and {rowsWithheld} row(s)"
                : $"{rowsWithheld} row(s)";

            var filters = kind == null
                ? capped.Select(x => x.Group.Split('/')[0])
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .Select(k => $"--kind {k}")
                    .ToList()
                : ["--under <path>"];

            var footer = $"# unresolved is a sample; withheld {withheld} (see: {string.Join(", ", filters)})";
            w.AddMarker(footer.Length <= 170 ? footer : footer[..167] + "...");
        }

        return Exit.Ok;
    }

    private static int TooMany(BudgetWriter w, int total)
    {
        w.AddMarker(IncompleteMarker(w, "narrow with --kind di, or raise --budget", 0, total));
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
