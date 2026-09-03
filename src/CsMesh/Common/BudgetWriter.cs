using System.Text;

namespace CsMesh.Common;

/// <summary>
/// Output writer enforcing an estimated token budget to prevent unbounded output.
/// </summary>
public sealed class BudgetWriter
{
    private readonly List<string> _lines = new();
    private readonly int _budget;

    public BudgetWriter(int budgetTokens) => _budget = budgetTokens;

    public int Tokens => Estimate(_lines);
    public bool Overflowed { get; private set; }

    public static int Estimate(IEnumerable<string> lines)
    {
        var chars = lines.Sum(l => l.Length + 1);
        return (int)Math.Ceiling(chars / 4.0);
    }

    public static int Estimate(string s) => (int)Math.Ceiling((s.Length + 1) / 4.0);

    /// <summary>
    /// Appends a line if within budget; returns false if the budget would be exceeded.
    /// </summary>
    public bool Add(string line)
    {
        if (Tokens + Estimate(line) > _budget)
        {
            Overflowed = true;
            return false;
        }

        _lines.Add(line);
        return true;
    }

    /// <summary>
    /// Forces line addition regardless of budget (e.g. headers or truncation warnings).
    /// </summary>
    public void Force(string line) => _lines.Add(line);

    public void Flush()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines)
        {
            sb.AppendLine(l);
        }
        Console.Out.Write(sb.ToString());
        Telemetry.Telemetry.Current.OutTokens = Tokens;
    }

    public IReadOnlyList<string> Lines => _lines;
}
