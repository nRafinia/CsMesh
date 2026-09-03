namespace CsMesh.Common;

/// <summary>
/// Diagnostic logger that writes to standard error to avoid polluting standard output streams.
/// </summary>
public static class Dbg
{
    public static bool On { get; set; }

    public static void Log(string msg)
    {
        if (!On) return;
        Console.Error.WriteLine($"[CsMesh {DateTime.Now:HH:mm:ss.fff}] {msg}");
    }
}
