using System.Text;
using CsMesh.Models;

namespace CsMesh.Common;

/// <summary>
/// Output writer enforcing an estimated token budget to prevent unbounded output.
/// Every text line may carry a parallel <see cref="QueryRow"/> so the same answer can be emitted
/// as JSON without a second traversal.
/// </summary>
public sealed class BudgetWriter(int budgetTokens, int markerReserve = 0)
{
    /// <summary>
    /// Tokens held back from content so the closing line always fits inside the budget it reports
    /// on. Content is capped at <see cref="Budget"/> minus this. A query that does not overflow
    /// emits no marker and spends none of the reserve on output; the reserve only reduces how much
    /// content fits before the answer is truncated.
    /// </summary>
    public const int CompletionMarkerReserve = 40;

    /// <summary>
    /// The most a single opening note may cost. Notes are truncated to it, so the pool a note draws
    /// from is bounded and the content cap it reduces is knowable before the query runs.
    /// </summary>
    public const int OpeningNoteMaxTokens = 60;

    private readonly List<string> _lines = [];
    private readonly List<QueryRow> _rows = [];
    private int _tokens;
    private int _contentTokens;
    private int _openingReserve;

    public int Tokens => _tokens;

    /// <summary>The cap this writer was built with, so a query can suggest one that fits.</summary>
    public int Budget => budgetTokens;

    /// <summary>Tokens reserved for the completeness marker; zero for writers that never emit one.</summary>
    public int MarkerReserve => markerReserve;

    /// <summary>
    /// Tokens actually spent on opening notes, held out of the content cap. Zero when the writer
    /// carried no note, so the common path reserves nothing for a note it will never write.
    /// </summary>
    public int OpeningReserve => _openingReserve;

    private int ContentCap => Math.Max(0, budgetTokens - markerReserve - _openingReserve);

    /// <summary>
    /// What is left of the content cap. Lets a caller price a heading and its first row together,
    /// so a section title is never printed with nothing under it.
    /// </summary>
    public int Remaining => Math.Max(0, ContentCap - _contentTokens);
    public bool Overflowed { get; private set; }

    private int _wouldBeTokens;

    /// <summary>
    /// The size the answer wanted on the first refusal: the emitted total plus the line that did not
    /// fit. Lower bound, not the full answer -- a query returns at the first refusal, so everything
    /// after it is never measured. Frozen at that moment, so the forced "INCOMPLETE" prose that
    /// follows does not inflate it. Equal to <see cref="Tokens"/> when nothing overflowed.
    /// </summary>
    public int WouldBeTokens => Overflowed ? _wouldBeTokens : _tokens;

    /// <summary>
    /// How far the wanted answer went past the budget the caller asked for; zero when nothing
    /// overflowed, or when the answer fit the requested budget but was cut to make room for the
    /// completion marker. Measured against <see cref="Budget"/>, not the content cap, because that
    /// is the number the caller chose and the one the marker reports.
    /// </summary>
    public int OverBudgetBy => Overflowed ? Math.Max(0, _wouldBeTokens - budgetTokens) : 0;

    /// <summary>The budget that would have fit this answer, completion marker included.</summary>
    public int SuggestedBudget => Overflowed ? _wouldBeTokens + markerReserve + 10 : budgetTokens;

    /// <summary>Rows emitted so far, excluding headers and warnings.</summary>
    public IReadOnlyList<QueryRow> Rows => _rows;
    public IReadOnlyList<string> Lines => _lines;

    public static int Estimate(string s) => (int)Math.Ceiling((s.Length + 1) / 4.0);
    public static int Estimate(IEnumerable<string> lines) => lines.Sum(Estimate);

    /// <summary>
    /// Appends a line if within the content cap; returns false if it would be exceeded.
    /// </summary>
    public bool Add(string line, QueryRow? row = null)
    {
        var cost = Estimate(line);
        if (_contentTokens + cost > ContentCap)
        {
            if (!Overflowed) _wouldBeTokens = _tokens + cost;
            Overflowed = true;
            return false;
        }

        _lines.Add(line);
        _tokens += cost;
        _contentTokens += cost;
        if (row != null) _rows.Add(row);
        return true;
    }

    /// <summary>
    /// Writes a note that must survive. It draws from its own pool rather than competing with the
    /// rows: the line is shortened to <paramref name="maxTokens"/> and always emitted, and its cost
    /// is held out of the content cap and the completion-marker reserve. The query then pays for the
    /// note in reduced content room, which is the point -- a version gap makes the rows themselves
    /// untrustworthy, so silently dropping either the note or the rows it warns about is the wrong
    /// trade. Call before any content is written, so the content cap is settled when rows begin.
    /// </summary>
    public bool AddOpeningNote(string line, int maxTokens = OpeningNoteMaxTokens)
    {
        if (maxTokens < 2) return false;

        var cost = Estimate(line);
        if (cost > maxTokens)
        {
            var maxChars = maxTokens * 4 - 1;
            line = line[..Math.Min(line.Length, maxChars)];
            cost = Estimate(line);
        }

        _lines.Add(line);
        _tokens += cost;
        _openingReserve += cost;
        return true;
    }

    /// <summary>
    /// A blank separator between sections. Cosmetic, so it never ends a query: when it does not fit
    /// it is skipped and the caller carries on to the line that actually matters. Guarded as
    /// <c>if (!w.Add("")) return ...</c> it was read as the end of the answer, and the closing line
    /// behind it was never attempted -- a capped answer lost the footer naming what it withheld
    /// whenever the separator landed exactly on the content boundary. A separator costs one token
    /// (Estimate("") == 1), so that window is one token wide and easy to step over.
    /// </summary>
    public void Separator()
    {
        var cost = Estimate("");
        if (_contentTokens + cost > ContentCap) return;

        _lines.Add("");
        _tokens += cost;
        _contentTokens += cost;
    }

    /// <summary>
    /// Writes the line that closes an answer: the completion marker on overflow, or the footer
    /// naming what a bounded sample withheld. This is the one line allowed past the content cap,
    /// because a closing line dropped for room is exactly the silent truncation the reserve exists
    /// to prevent -- and a capped answer at exit 0 has no completion marker to yield to.
    ///
    /// Forced headers can consume the reserve before the closing line is reached, so if the full
    /// line will not fit it is shortened to the room that is left rather than dropped. The invariant
    /// is that an incomplete or capped answer is always closed; only a budget already overrun by
    /// forced content leaves no room, and then it returns false.
    /// </summary>
    public bool AddMarker(string line)
    {
        var cost = Estimate(line);
        if (_tokens + cost > budgetTokens)
        {
            var room = budgetTokens - _tokens;
            if (room < 2) return false;

            var maxChars = Math.Max(0, room * 4 - 1);
            line = line[..Math.Min(line.Length, maxChars)];
            cost = Estimate(line);
            if (_tokens + cost > budgetTokens) return false;
        }

        _lines.Add(line);
        _tokens += cost;
        return true;
    }

    /// <summary>
    /// Forces line addition regardless of budget (e.g. headers or truncation warnings).
    /// </summary>
    public void Force(string line, QueryRow? row = null)
    {
        var cost = Estimate(line);
        _lines.Add(line);
        _tokens += cost;
        _contentTokens += cost;
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
        Telemetry.Telemetry.Current.WouldBeTokens = WouldBeTokens;
    }
}
