using System.Text.Json;
using CsMesh.Common;

namespace CsMesh.Commands;

public static class UsageCommand
{
    /// <summary>
    /// Commands that answer a question about the graph. Excludes index, doctor, skill and the
    /// rest, which are not what an agent reaches for mid-task and would make the coverage read
    /// look better than it is.
    /// </summary>
    /// <summary>
    /// Commands that resolve a symbol argument, so exit 1 means "not found" and exit 3 means the
    /// name was not specific enough. Everywhere else those codes mean something else.
    /// </summary>
    private static readonly string[] SymbolLookups =
    {
        "trace", "impl", "blast-radius", "blast", "context", "path", "why"
    };

    private static readonly string[] QueryCommands =
    {
        "map", "trace", "impl", "blast-radius", "entrypoints", "context",
        "path", "cycles", "unresolved", "diff", "changes", "silence"
    };

    public static int Execute(string root, Options opt)
    {
        var all = Telemetry.Telemetry.Read(root);
        if (opt.Flag("json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(all, AppJsonContext.Default.ListInvocation));
            return Exit.Ok;
        }

        var days = opt.Int("days", 7);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var rows = all.Where(i => DateTimeOffset.TryParse(i.Ts, out var t) && t >= since).ToList();

        if (opt.Flag("tail"))
        {
            // Reads its own flag value. It used to read --n, so --tail 5 silently returned 20 rows.
            foreach (var i in rows.TakeLast(opt.Int("tail", 10)))
            {
                Console.WriteLine($"{i.Ts[..Math.Min(19, i.Ts.Length)]}  {i.Caller,-18} {i.Cmd,-13} exit={i.Exit} {i.Ms,5}ms {i.OutTokens,5}tok  {i.Args}");
            }
            return Exit.Ok;
        }

        if (rows.Count == 0)
        {
            Console.WriteLine($"no invocations in the last {days} day(s).");
            Console.WriteLine("If agents should be calling this, check that the skill is installed: csmesh doctor");
            return Exit.Ok;
        }

        var agentRows = rows.Where(r => r.Caller != "human").ToList();
        Console.WriteLine($"csmesh usage, last {days} day(s)");
        Console.WriteLine($"  invocations       {rows.Count}");
        Console.WriteLine($"  agent-attributed  {agentRows.Count} ({FormattingUtils.Pct(agentRows.Count, rows.Count)})");
        Console.WriteLine($"  distinct sessions {rows.Select(r => r.Session).Where(s => s != null).Distinct().Count()}");
        Console.WriteLine();

        Console.WriteLine("  by caller");
        foreach (var group in rows.GroupBy(r => r.Caller).OrderByDescending(x => x.Count()))
        {
            Console.WriteLine($"    {group.Key,-20} {group.Count(),5}   via {string.Join("/", group.Select(x => x.CallerVia).Distinct().Take(2))}");
        }

        Console.WriteLine();
        Console.WriteLine("  by command");
        foreach (var group in rows.GroupBy(r => r.Cmd).OrderByDescending(x => x.Count()))
        {
            var med = FormattingUtils.Median(group.Select(x => (double)x.Ms));
            var tokenMed = FormattingUtils.Median(group.Select(x => (double)x.OutTokens));
            Console.WriteLine($"    {group.Key,-20} {group.Count(),5}   median {med:F0}ms, {tokenMed:F0} tok");
        }

        Console.WriteLine();
        Console.WriteLine("  exit codes");
        foreach (var group in rows.GroupBy(r => r.Exit).OrderBy(x => x.Key))
        {
            Console.WriteLine($"    {group.Key} {FormattingUtils.ExitName(group.Key),-14} {group.Count(),5}");
        }

        // Only commands that take a symbol and can fail to find it. Defined by inclusion rather
        // than exclusion: counting 'index' as an answered query would put the hit rate wherever
        // the re-index count happened to be, and 'silence' returning 1 means it explained a real
        // absence, which is the command working rather than failing.
        var lookups = rows.Where(r => SymbolLookups.Contains(r.Cmd, StringComparer.Ordinal)).ToList();

        var over = lookups.Count(r => r.Exit == Exit.OverBudget);
        var amb = lookups.Count(r => r.Exit == Exit.Ambiguous);
        var miss = lookups.Count(r => r.Exit == Exit.NotFound);
        var crashed = rows.Count(r => r.Exit == Exit.Internal);

        if (lookups.Count > 0)
        {
            // The one number worth watching. If agents are calling csmesh and mostly getting
            // answers, the tool works and the question is whether anyone knows about it. If they
            // are mostly getting 1 and 3, the tool is being called and failing, which is a
            // different problem with a different fix.
            var answeredLookups = lookups.Count(r => r.Exit == Exit.Ok);
            Console.WriteLine();
            Console.WriteLine("  hit rate  (trace, impl, blast-radius, context, path)");
            Console.WriteLine($"    answered          {answeredLookups}/{lookups.Count} ({FormattingUtils.Pct(answeredLookups, lookups.Count)})");
            Console.WriteLine($"    not found         {miss}");
            Console.WriteLine($"    ambiguous         {amb}");
            Console.WriteLine($"    over budget       {over}");
        }

        var used = rows.Select(r => r.Cmd).ToHashSet(StringComparer.Ordinal);
        var unused = QueryCommands.Where(c => !used.Contains(c)).ToList();

        if (unused.Count > 0)
        {
            // A command nobody runs is either not useful or not discoverable, and the fix differs.
            // Agents reach for what the skill tells them about, so this is a read on the skill.
            Console.WriteLine();
            Console.WriteLine("  never used        " + string.Join(", ", unused));
            Console.WriteLine("                    re-run 'csmesh skill --install' if the rules are out of date");
        }

        Console.WriteLine();
        Console.WriteLine("  diagnostics");
        if (agentRows.Count == 0)
        {
            Console.WriteLine("    * zero agent calls: skill may not be installed or matched.");
        }

        if (crashed > 0)
        {
            Console.WriteLine($"    * {crashed} invocation(s) ended in an internal error; re-run with --debug for a stack trace.");
        }

        if (over > lookups.Count * 0.15 && lookups.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(over, lookups.Count)} over budget. Narrow with --under before raising --budget;");
            Console.WriteLine("      trace already names a depth that fits when it overflows.");
        }

        if (amb > lookups.Count * 0.2 && lookups.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(amb, lookups.Count)} ambiguous: use qualified Type.Member names to prevent collisions.");
        }

        if (miss > lookups.Count * 0.25 && lookups.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(miss, lookups.Count)} of lookups not found. Before assuming a name mismatch,");
            Console.WriteLine("      run 'csmesh silence <symbol>' on one of them: the symbol may be a package type,");
            Console.WriteLine("      or the solution may not have been built when the index was made.");
        }

        var explained = rows.Count(r => r.Cmd is "silence" or "why-not");
        if (explained > 0 && miss > 0)
        {
            Console.WriteLine($"    * {explained} silence call(s): failed lookups are being investigated rather than abandoned.");
        }

        if (rows.Count > 10 && !used.Contains("map"))
        {
            Console.WriteLine("    * 'map' was never called. Agents that start with grep or a file listing have not");
            Console.WriteLine("      been told the graph can orient them; check 'csmesh doctor' for skill installation.");
        }

        var answered = rows.Where(r => r.Exit == Exit.Ok).ToList();
        if (answered.Count > 0)
        {
            var tokensReturned = answered.Sum(r => r.OutTokens);
            var filesHinted = answered.Sum(r => r.FilesReferenced);
            Console.WriteLine($"    * {answered.Count} answered queries returned {tokensReturned} tokens total and referenced {filesHinted} file locations.");
        }

        return Exit.Ok;
    }
}
