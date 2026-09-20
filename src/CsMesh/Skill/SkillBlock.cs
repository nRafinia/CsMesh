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
}
