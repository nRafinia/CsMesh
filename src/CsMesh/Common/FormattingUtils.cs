namespace CsMesh.Common;

/// <summary>
/// Common formatting and statistical calculation utilities.
/// </summary>
public static class FormattingUtils
{
    /// <summary>
    /// Integer percentage, floored. {value:F0} rounded 199/200 to "100%", which reads exactly like
    /// a complete result; integer division floors, so 100% is reserved for a true 100%.
    /// </summary>
    public static string Pct(int numerator, int denominator) =>
        denominator == 0 ? "0%" : $"{100L * numerator / denominator}%";

    public static double Median(IEnumerable<double> values)
    {
        var list = values.OrderBy(x => x).ToList();
        if (list.Count == 0) return 0;
        var mid = list.Count / 2;
        return list.Count % 2 == 1 ? list[mid] : (list[mid - 1] + list[mid]) / 2.0;
    }

    public static string ExitName(int exitCode) => exitCode switch
    {
        Exit.Ok => "ok",
        Exit.NotFound => "not-found",
        Exit.OverBudget => "over-budget",
        Exit.Ambiguous => "ambiguous",
        Exit.NoIndex => "no-index",
        Exit.Usage => "usage-error",
        Exit.Internal => "internal-error",
        _ => "unknown"
    };
}
