using System.Diagnostics;
using System.Text.Json;
using CsMesh.Common;

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

    public static void End(int exit)
    {
        if (Disabled || string.IsNullOrEmpty(Current.Root)) return;

        Current.Exit = exit;
        Current.Ms = Clock.ElapsedMilliseconds;

        try
        {
            var dir = Path.Combine(Current.Root, ".csmesh");
            Directory.CreateDirectory(dir);

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
                var invocation = JsonSerializer.Deserialize(line, AppJsonContext.Default.Invocation);
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
