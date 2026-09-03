namespace CsMesh.Common;

/// <summary>
/// Common formatting and statistical calculation utilities.
/// </summary>
public static class FormattingUtils
{
    public static string Pct(int numerator, int denominator) =>
        denominator == 0 ? "0%" : $"{100.0 * numerator / denominator:F0}%";

    public static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(x => x).ToList();
        return list.Count == 0 ? 0 : list[list.Count / 2];
    }

    public static string ExitName(int exitCode) => exitCode switch
    {
        Exit.Ok => "ok",
        Exit.NotFound => "not-found",
        Exit.OverBudget => "over-budget",
        Exit.Ambiguous => "ambiguous",
        Exit.NoIndex => "no-index",
        _ => "usage-error"
    };
}
