namespace CsMesh.Common;

/// <summary>
/// Collects a command's output so the same code path can render a terminal answer or a JSON one.
///
/// The query commands already had this, in BudgetWriter. doctor and index did not: they wrote
/// straight to the console, which is why they were the two commands --json silently ignored. An
/// agent asking whether the index is usable had to parse the prose, and a caller wiring csmesh to
/// anything other than a terminal had to special-case exactly these two.
///
/// In JSON mode nothing reaches stdout as it is produced. That matters beyond tidiness: the MCP
/// server multiplexes JSON-RPC frames over stdout, and one stray WriteLine from a command would
/// corrupt the stream rather than merely look untidy.
/// </summary>
public sealed class Emit(bool json)
{
    public List<string> Lines { get; } = [];

    public bool Json { get; } = json;

    public void Line(string text = "")
    {
        Lines.Add(text);
        if (!Json) Console.WriteLine(text);
    }
}
