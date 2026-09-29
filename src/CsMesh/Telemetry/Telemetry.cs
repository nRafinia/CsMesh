using System.Diagnostics;
using System.Text.Json;
using CsMesh.Common;
using CsMesh.Storage;

namespace CsMesh.Telemetry;

/// <summary>
/// Records local invocation metrics and diagnostics in the repository's `.csmesh/usage.jsonl` file.
/// </summary>
public static class Telemetry
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static Invocation Current { get; } = new() { Ts = DateTimeOffset.UtcNow.ToString("O") };
    public static bool Disabled { get; set; }

    public static string LogPath(string root) => Path.Combine(root, ".csmesh", "usage.jsonl");

    public static void Begin(string cmd, string[] args)
    {
        Current.Cmd = cmd;
        Current.Args = string.Join(' ', args.Where(a => !a.StartsWith("--no-telemetry")));

        var (caller, via) = CallerDetector.Detect();
        Current.Caller = caller;
        Current.CallerVia = via;
        Current.Tty = !Console.IsOutputRedirected;
        Current.Session = CallerDetector.SessionHint();
        Current.Parents = CallerDetector.ParentChain();
    }

    /// <summary>
    /// Appends the finished invocation to a <c>.csmesh</c> that already exists, and never creates
    /// one.
    ///
    /// The directory belongs to the repository, not to telemetry. Creation used to happen here
    /// through <c>CsMeshDir.Ensure</c>, so any command that resolved a root and then failed -- a
    /// usage error in a plain folder, exit 64 in 3 ms -- left a <c>.csmesh</c> behind. While the
    /// root rule accepted the bare directory, that stray folder then marked the parent as a
    /// repository for every unmarked folder beneath it. Commands that write state (the graph, the
    /// review baseline) ensure the directory themselves; one that only logs must leave the tree as
    /// it found it.
    /// </summary>
    public static void End(int exit)
    {
        if (Disabled || string.IsNullOrEmpty(Current.Root)) return;

        // No directory, no log. Creating it is what put a marker where the user had none.
        if (!Directory.Exists(CsMeshDir.For(Current.Root))) return;

        Current.Exit = exit;
        Current.Ms = Clock.ElapsedMilliseconds;
        Current.SchemaVersion = Invocation.CurrentSchemaVersion;

        try
        {
            var line = JsonSerializer.Serialize(Current, AppJsonContext.Default.Invocation);
            using var fs = new FileStream(LogPath(Current.Root), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine(line);
        }
        catch
        {
            // Telemetry failure should not disrupt main execution
        }
    }

    public static List<Invocation> Read(string root)
    {
        var path = LogPath(root);
        var list = new List<Invocation>();
        if (!File.Exists(path)) return list;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var invocation = JsonSerializer.Deserialize(line, TelemetryReadContext.Default.Invocation);
                if (invocation != null) list.Add(invocation);
            }
            catch
            {
                // Skip corrupted log lines
            }
        }

        return list;
    }
}
