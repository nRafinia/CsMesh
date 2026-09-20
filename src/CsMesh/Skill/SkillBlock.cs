namespace CsMesh.Skill;

/// <summary>
/// The delimited rules block that <c>csmesh install</c> writes into files the user also edits, and
/// the one renderer both <c>install</c> and <c>doctor</c> call.
///
/// The block carries no version and no hash, so a rules file written by an older binary is
/// indistinguishable by eye from one this build would write. An AGENTS.md left over from 0.4.1
/// keeps steering agents with the budgets that release recommended -- trace 600, impl 300, path
/// 400 -- after the binary's own defaults moved, and nothing surfaces it, because the agent reads
/// the stale block rather than the installed binary. Rendering in one place is what lets doctor
/// regenerate exactly what install writes and compare against it.
/// </summary>
public static class SkillBlock
{
    public const string StartTag = "<!-- csmesh-instructions -->";
    public const string EndTag = "<!-- /csmesh-instructions -->";

    /// <summary>
    /// The block as install writes it: the start marker, the trimmed body, the end marker.
    ///
    /// Pure formatting. No machine name, path or date enters, so the same body always renders the
    /// same text and a comparison against an installed block is meaningful rather than a coin toss.
    /// </summary>
    public static string Render(string blockContent) =>
        $"{StartTag}\n{blockContent.Trim()}\n{EndTag}";

    /// <summary>
    /// The block installed in <paramref name="fileText"/>, from the start marker through the end
    /// marker, or null when the file carries no complete block. A half-block is not a block:
    /// reporting one would invent drift install never wrote, and a file with no block at all is
    /// "not installed", not stale.
    /// </summary>
    public static string? Extract(string fileText)
    {
        var start = fileText.IndexOf(StartTag, StringComparison.Ordinal);
        if (start < 0) return null;

        var end = fileText.IndexOf(EndTag, start, StringComparison.Ordinal);
        return end < 0 ? null : fileText[start..(end + EndTag.Length)];
    }

    /// <summary>
    /// Makes two blocks comparable across the ways the same text reaches disk: a UTF-8 BOM an
    /// editor left, CRLF line endings from git autocrlf on Windows, doubled CRs an editor produced
    /// by re-encoding an already-CRLF file, and trailing whitespace. Without this, every
    /// checked-out block would read as drift and the warning would be noise on exactly the machines
    /// it is meant to help.
    /// </summary>
    public static string Normalize(string text)
    {
        if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];

        // Per line, not a single Replace on the whole text: a file that already carried CRLF and
        // was converted to CRLF again contains "\r\r\n", and trimming the trailing CR off each
        // line maps that to one newline instead of two.
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select(line => line.TrimEnd())).TrimEnd();
    }
}
