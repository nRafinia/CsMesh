using System.Text.Json;
using CsMesh.Common;

namespace CsMesh.Commands;

public static class UsageCommand
{
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

        var over = rows.Count(r => r.Exit == Exit.OverBudget);
        var amb = rows.Count(r => r.Exit == Exit.Ambiguous);
        var miss = rows.Count(r => r.Exit == Exit.NotFound);
        var crashed = rows.Count(r => r.Exit == Exit.Internal);

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

        if (over > rows.Count * 0.15 && rows.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(over, rows.Count)} over budget: consider increasing default budget or narrowing queries.");
        }

        if (amb > rows.Count * 0.2 && rows.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(amb, rows.Count)} ambiguous: use qualified Type.Member names to prevent collisions.");
        }

        if (miss > rows.Count * 0.25 && rows.Count > 5)
        {
            Console.WriteLine($"    * {FormattingUtils.Pct(miss, rows.Count)} not found: index may need refresh (csmesh index) or symbol name mismatch.");
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
