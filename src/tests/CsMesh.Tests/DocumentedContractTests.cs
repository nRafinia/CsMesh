using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// The contract lives in prose files nothing compiles: the exit tables and the default-budget line
/// are what an agent branches on, and they can drift from the code silently. These read the files
/// and compare them to the code, the way <c>SkillTextTests</c> compares the root SKILL.md to
/// <c>SkillText.Markdown</c>. A contract-changing release should ship with the drift guard already
/// in place, not add it afterwards.
///
/// These are shape checks, and their limits are the point of stating them:
/// <list type="bullet">
/// <item>The exit-set checks see that every emitted code is present and no invented one is. They
/// cannot see a code listed with the wrong meaning or remedy, or a code emitted as a literal
/// instead of through an <see cref="Exit"/> constant -- reflection only reads the declared
/// constants.</item>
/// <item>The budget-line check sees that each named default equals the cap the writer gets. It
/// cannot see a default that is numerically right but attributed to the wrong command, or a prose
/// sentence that describes the right number wrongly; that is <c>HelpTextTests</c>' per-command
/// wording, and no mechanical check covers the sentence itself.</item>
/// <item>The review check ties its help text to the one constant the command uses. It cannot prove
/// that constant is the only budget review ever applies.</item>
/// </list>
/// </summary>
public sealed class DocumentedContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CsMesh.slnx"))) dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    /// <summary>The exit codes the code can emit, read off the <see cref="Exit"/> constants.</summary>
    private static IReadOnlySet<int> EmittedExitCodes() => typeof(Exit)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(int))
        .Select(f => (int)f.GetRawConstantValue()!)
        .ToHashSet();

    /// <summary>
    /// Codes in the first markdown table after a heading mentioning "Exit". The row shape is
    /// <c>| 0 | ... |</c> or <c>| `0` | ... |</c>, which is why the heading scoping matters: an
    /// integer-first-column table elsewhere is not an exit table.
    /// </summary>
    private static HashSet<int> MarkdownExitCodes(string text)
    {
        var codes = new HashSet<int>();
        var inSection = false;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('#'))
            {
                inSection = line.Contains("exit", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection) continue;

            var match = Regex.Match(line, @"^\|\s*`?(\d+)`?\s*\|");
            if (match.Success) codes.Add(int.Parse(match.Groups[1].Value));
        }

        return codes;
    }

    /// <summary>The codes in the landing page's exit grid, delimited by its section.</summary>
    private static HashSet<int> HtmlExitCodes(string text)
    {
        var start = text.IndexOf("Exit codes an agent can branch on", StringComparison.Ordinal);
        Assert.True(start >= 0, "the landing page should have an exit-code section");

        var end = text.IndexOf("</section>", start, StringComparison.Ordinal);
        var section = text[start..(end < 0 ? text.Length : end)];

        return Regex.Matches(section, @"<kbd>(\d+)</kbd>")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToHashSet();
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("npm/README.md")]
    [InlineData("SKILL.md")]
    public void A_markdown_exit_table_lists_exactly_the_emitted_exit_codes(string file)
    {
        Assert.Equal(EmittedExitCodes().Order(), MarkdownExitCodes(Read(file)).Order());
    }

    [Fact]
    public void The_landing_page_exit_grid_lists_exactly_the_emitted_exit_codes()
    {
        Assert.Equal(EmittedExitCodes().Order(), HtmlExitCodes(Read("docs/index.html")).Order());
    }

    private static readonly string[] QueryKinds =
    [
        "blast", "changes", "context", "cycles", "diff", "entrypoints",
        "impl", "map", "path", "silence", "trace", "unresolved", "where"
    ];

    /// <summary>
    /// Parses "Default budgets: `impl` 600, `path` 500, ... everything else 800." into a kind map
    /// plus a default. Named kinds win; anything absent falls to the "everything else" value.
    /// </summary>
    private static Dictionary<string, int> DocumentedDefaultBudgets(string text)
    {
        var paragraph = Paragraph(text, "Default budgets:");
        var map = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var segment in paragraph.Split(','))
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;

            var number = Regex.Match(trimmed, @"(\d+)\s*\.?\s*$");
            if (!number.Success) continue;
            var value = int.Parse(number.Groups[1].Value);

            if (trimmed.StartsWith("everything else", StringComparison.OrdinalIgnoreCase))
            {
                map["__default"] = value;
                continue;
            }

            foreach (Match name in Regex.Matches(trimmed, @"`([a-z-]+)`"))
                map[name.Groups[1].Value] = value;
        }

        return map;
    }

    /// <summary>A line and the lines it wraps onto, joined; the README line wraps after silence.</summary>
    private static string Paragraph(string text, string startsWith)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith(startsWith, StringComparison.Ordinal)) continue;

            var joined = new StringBuilder(lines[i]);
            for (var j = i + 1; j < lines.Length && lines[j].Trim().Length > 0 && !lines[j].StartsWith('#'); j++)
            {
                joined.Append(' ').Append(lines[j].Trim());
            }

            return joined.ToString();
        }

        Assert.Fail($"'{startsWith}' not found");
        return "";
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("npm/README.md")]
    public void The_default_budget_line_matches_the_cap_each_command_gets(string file)
    {
        var documented = DocumentedDefaultBudgets(Read(file));
        Assert.True(documented.ContainsKey("__default"), "the line should name an 'everything else' default");

        foreach (var kind in QueryKinds)
        {
            var expected = documented.TryGetValue(kind, out var named) ? named : documented["__default"];
            Assert.Equal(expected, QueryCommand.WriterFor(kind, new Options([])).Budget);
        }
    }

    /// <summary>
    /// review bypasses <see cref="QueryCommand.WriterFor"/>, so its documented default was the one
    /// budget no test compared to anything. The constant is the code side of that comparison.
    /// </summary>
    [Fact]
    public void The_review_help_names_the_default_review_actually_uses()
    {
        Assert.Contains($"default: {ReviewCommand.DefaultBudget}", HelpCommand.ReviewHelp, StringComparison.Ordinal);
    }
}
