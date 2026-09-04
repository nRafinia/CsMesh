using System.Text;
using CsMesh.Models;

namespace CsMesh.Common;

/// <summary>
/// Output writer enforcing an estimated token budget to prevent unbounded output.
/// Every text line may carry a parallel <see cref="QueryRow"/> so the same answer can be emitted
/// as JSON without a second traversal.
/// </summary>
public sealed class BudgetWriter(int budgetTokens)
{
    private readonly List<string> _lines = [];
    private readonly List<QueryRow> _rows = [];
    private int _tokens;

    public int Tokens => _tokens;
    public bool Overflowed { get; private set; }

    /// <summary>Rows emitted so far, excluding headers and warnings.</summary>
    public IReadOnlyList<QueryRow> Rows => _rows;
    public IReadOnlyList<string> Lines => _lines;

    public static int Estimate(string s) => (int)Math.Ceiling((s.Length + 1) / 4.0);
    public static int Estimate(IEnumerable<string> lines) => lines.Sum(Estimate);

    /// <summary>
    /// Appends a line if within budget; returns false if the budget would be exceeded.
    /// </summary>
    public bool Add(string line, QueryRow? row = null)
    {
        var cost = Estimate(line);
        if (_tokens + cost > budgetTokens)
        {
            Overflowed = true;
            return false;
        }

        _lines.Add(line);
        _tokens += cost;
        if (row != null) _rows.Add(row);
        return true;
    }

    /// <summary>
    /// Forces line addition regardless of budget (e.g. headers or truncation warnings).
    /// </summary>
    public void Force(string line, QueryRow? row = null)
    {
        _lines.Add(line);
        _tokens += Estimate(line);
        if (row != null) _rows.Add(row);
    }

    /// <summary>
    /// Distinct source files the answer pointed the caller at.
    /// </summary>
    public int DistinctFiles => _rows
        .Where(r => !string.IsNullOrEmpty(r.File))
        .Select(r => r.File!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public void Flush()
    {
        var sb = new StringBuilder();
        foreach (var l in _lines) sb.AppendLine(l);
        Console.Out.Write(sb.ToString());
        Telemetry.Telemetry.Current.OutTokens = _tokens;
    }
}
