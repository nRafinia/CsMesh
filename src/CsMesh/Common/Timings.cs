using System.Diagnostics;

namespace CsMesh.Common;

/// <summary>
/// Opt-in phase timing for a run, turned on with the environment variable <c>CSMESH_TIMINGS=1</c>.
///
/// A cold first index on a freshly built or freshly copied binary is several times slower than
/// every run after it, and the reason sat somewhere between process start and the graph being
/// written with no way to tell which phase held it. With the variable set, each phase writes one
/// line to stderr carrying the milliseconds it took and, where it means something, a count -- the
/// reference set's DLL and byte totals, the resolved runtime path. A user reporting a slow run can
/// then say where the time went instead of that it was slow.
///
/// Unset -- the default -- nothing is printed and nothing is measured, so an ordinary invocation
/// pays for none of this.
///
/// stderr only, deliberately. stdout is the JSON and MCP transport, and one timing line on it would
/// corrupt a frame rather than merely look untidy. This is a diagnostic for a human, not part of
/// any answer.
/// </summary>
public static class Timings
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static readonly Dictionary<string, double> Buckets = new(StringComparer.Ordinal);

    /// <summary>
    /// Read on each use rather than cached. The default is off, and a test that turns it on for one
    /// run in a long-lived process would otherwise find the first off-run had latched it off for
    /// the rest of the suite.
    /// </summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("CSMESH_TIMINGS") == "1";

    private static readonly PhaseScope Off = new("", accumulate: false, enabled: false);

    /// <summary>
    /// Records the time from the operating system's process-start stamp to this call, which is the
    /// first thing Main does. The shell's wall time starts here too, so a phase report can say how
    /// much of a slow launch happened before any csmesh code ran -- the part a virus scanner or a
    /// cold loader owns, not the indexer.
    /// </summary>
    public static void Begin()
    {
        if (!Enabled) return;

        DateTime started;
        try { started = Process.GetCurrentProcess().StartTime; }
        catch (Exception ex) { Write("start-to-main", double.NaN, $"start-time-unavailable ({ex.GetType().Name})"); return; }

        Write("start-to-main", (DateTime.Now - started).TotalMilliseconds, null);
    }

    /// <summary>Times one phase. Dispose writes the line; <see cref="PhaseScope.Detail"/> enriches it.</summary>
    public static PhaseScope Phase(string name) => Enabled ? new PhaseScope(name, accumulate: false) : Off;

    /// <summary>
    /// Times one step into a named bucket instead of printing it immediately, for a phase whose two
    /// halves sit on either side of other work -- usings are synthesized before the compilations
    /// that carry the IVT attributes. <see cref="Flush"/> writes the summed line once.
    /// </summary>
    public static PhaseScope Accumulate(string name) => Enabled ? new PhaseScope(name, accumulate: true) : Off;

    /// <summary>Writes the summed line for a bucket, if anything was accumulated into it.</summary>
    public static void Flush(string name)
    {
        if (!Enabled) return;
        if (!Buckets.TryGetValue(name, out var ms)) return;

        Write(name, ms, null);
    }

    /// <summary>The whole run, from the process-start stamp, after telemetry has recorded its own exit.</summary>
    public static void End()
    {
        if (!Enabled) return;

        DateTime started;
        try { started = Process.GetCurrentProcess().StartTime; }
        catch { Write("total", Stopwatch.GetElapsedTime(Start).TotalMilliseconds, null); return; }

        Write("total", (DateTime.Now - started).TotalMilliseconds, null);
    }

    internal static void Finish(string name, double ms, bool accumulate, string? detail)
    {
        if (!Enabled) return;

        if (accumulate)
        {
            Buckets[name] = Buckets.GetValueOrDefault(name) + ms;
            return;
        }

        Write(name, ms, detail);
    }

    private static void Write(string phase, double ms, string? detail)
    {
        var value = double.IsNaN(ms) ? "n/a" : $"{ms:F1}ms";
        Console.Error.WriteLine($"[csmesh timings] {phase} {value}{(detail is null ? "" : " " + detail)}");
    }

    /// <summary>A measured region. Created only when timings are on; otherwise a shared no-op.</summary>
    public sealed class PhaseScope : IDisposable
    {
        private readonly string _name;
        private readonly bool _accumulate;
        private readonly bool _enabled;
        private readonly long _start;
        private string? _detail;

        internal PhaseScope(string name, bool accumulate, bool enabled = true)
        {
            _name = name;
            _accumulate = accumulate;
            _enabled = enabled;
            _start = enabled ? Stopwatch.GetTimestamp() : 0;
        }

        /// <summary>Appends a count or path to the line this scope will write.</summary>
        public PhaseScope Detail(string detail)
        {
            if (_enabled) _detail = detail;
            return this;
        }

        public void Dispose()
        {
            if (!_enabled) return;

            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            Finish(_name, ms, _accumulate, _detail);
        }
    }
}
