using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

[Collection("console-capture")]
public sealed class SkillCommandTests : IDisposable
{
    private readonly string _root;

    public SkillCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-skill-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void SkillWithoutFlagsDisplaysHelpNotMarkdown()
    {
        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exit = CliRunner.Run(["skill"]);
            Assert.Equal(Exit.Ok, exit);
            var output = sw.ToString();

            Assert.Contains("csmesh skill - Display skill markdown", output);
            Assert.Contains("USAGE:", output);
            Assert.DoesNotContain("# csmesh: C# structural code intelligence", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void SkillWithShowOrPrintDisplaysMarkdown()
    {
        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            var exit = CliRunner.Run(["skill", "--show"]);
            Assert.Equal(Exit.Ok, exit);
            var output = sw.ToString();

            Assert.Contains("name: csmesh", output);
            Assert.Contains("# csmesh", output);
        }
        finally
        {
            Console.SetOut(origOut);
        }
    }

    [Fact]
    public void MutuallyExclusiveInstallAndUninstallFlagsReturnUsageError()
    {
        Assert.Equal(Exit.Usage, CliRunner.Run(["skill", "--install", "--uninstall"]));
        Assert.Equal(Exit.Usage, CliRunner.Run(["install", "--uninstall"]));
        Assert.Equal(Exit.Usage, CliRunner.Run(["uninstall", "--install"]));
    }

    [Fact]
    public void DedicatedSkillFilesInstallAndUninstallCorrectly()
    {
        var skillMd = Path.Combine(_root, ".agents", "skills", "csmesh", "SKILL.md");
        var rulesMd = Path.Combine(_root, ".agents", "rules", "csmesh.md");

        // Install
        var installExit = SkillCommand.Execute(_root, new Options(["--agent", "antigravity"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, installExit);
        Assert.True(File.Exists(skillMd));
        Assert.True(File.Exists(rulesMd));

        // Uninstall
        var uninstallExit = SkillCommand.Execute(_root, new Options(["--agent", "antigravity"]), SkillMode.Uninstall);
        Assert.Equal(Exit.Ok, uninstallExit);
        Assert.False(File.Exists(skillMd));
        Assert.False(File.Exists(rulesMd));
    }

    [Fact]
    public void SharedBlockFilesInstallAndUninstallPreservingOtherContent()
    {
        var agentsMd = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(agentsMd, "# Custom User Rules\nDo not break production.\n");

        // Install
        var installExit = SkillCommand.Execute(_root, new Options(["--agent", "opencode"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, installExit);
        Assert.True(File.Exists(agentsMd));

        var contentAfterInstall = File.ReadAllText(agentsMd);
        Assert.Contains("# Custom User Rules", contentAfterInstall);
        Assert.Contains("<!-- csmesh-instructions -->", contentAfterInstall);

        // Uninstall
        var uninstallExit = SkillCommand.Execute(_root, new Options(["--agent", "opencode"]), SkillMode.Uninstall);
        Assert.Equal(Exit.Ok, uninstallExit);
        Assert.True(File.Exists(agentsMd));

        var contentAfterUninstall = File.ReadAllText(agentsMd);
        Assert.Contains("# Custom User Rules", contentAfterUninstall);
        Assert.DoesNotContain("<!-- csmesh-instructions -->", contentAfterUninstall);
    }

    [Fact]
    public void OpencodeInstallAndUninstallManagesRulesCommandsAndMcpConfig()
    {
        var skill = Path.Combine(_root, ".opencode", "skills", "csmesh", "SKILL.md");
        var rules = Path.Combine(_root, ".opencode", "rules", "csmesh.md");
        var command = Path.Combine(_root, ".opencode", "commands", "csmesh.md");
        var mcp = Path.Combine(_root, ".opencode", "opencode.json");

        // Install
        var installExit = SkillCommand.Execute(_root, new Options(["--agent", "opencode"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, installExit);
        Assert.True(File.Exists(skill));
        Assert.True(File.Exists(rules));
        Assert.True(File.Exists(command));
        Assert.True(File.Exists(mcp));

        var mcpContent = File.ReadAllText(mcp);
        Assert.Contains("csmesh", mcpContent);
        Assert.Contains("local", mcpContent);

        // Uninstall
        var uninstallExit = SkillCommand.Execute(_root, new Options(["--agent", "opencode"]), SkillMode.Uninstall);
        Assert.Equal(Exit.Ok, uninstallExit);
        Assert.False(File.Exists(skill));
        Assert.False(File.Exists(rules));
        Assert.False(File.Exists(command));

        var mcpAfter = File.ReadAllText(mcp);
        Assert.DoesNotContain("csmesh", mcpAfter);
    }

    [Fact]
    public void McpIntegrationCanBeInstalledAndUninstalledViaInstallAndUninstallCommands()
    {
        var mcpJson = Path.Combine(_root, ".mcp.json");

        // Install MCP
        var installExit = SkillCommand.Execute(_root, new Options(["--mcp"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, installExit);
        Assert.True(File.Exists(mcpJson));
        Assert.Contains("csmesh", File.ReadAllText(mcpJson));

        // Uninstall MCP
        var uninstallExit = SkillCommand.Execute(_root, new Options(["--mcp"]), SkillMode.Uninstall);
        Assert.Equal(Exit.Ok, uninstallExit);
        Assert.DoesNotContain("\"csmesh\"", File.ReadAllText(mcpJson));
    }

    [Fact]
    public void SkillUninstallWorksDirectlyWithoutInstallFlag()
    {
        var mdcPath = Path.Combine(_root, ".cursor", "rules", "csmesh.mdc");

        // Install via skill --install
        var installExit = SkillCommand.Execute(_root, new Options(["--install", "--agent", "cursor"]), SkillMode.Skill);
        Assert.Equal(Exit.Ok, installExit);
        Assert.True(File.Exists(mdcPath));

        // Uninstall via skill --uninstall directly
        var uninstallExit = SkillCommand.Execute(_root, new Options(["--uninstall", "--agent", "cursor"]), SkillMode.Skill);
        Assert.Equal(Exit.Ok, uninstallExit);
        Assert.False(File.Exists(mdcPath));
    }

    [Fact]
    public void VsCodeAndRiderAgentTargetsWorkCorrectly()
    {
        var copilotInstructions = Path.Combine(_root, ".github", "copilot-instructions.md");
        var agentsMd = Path.Combine(_root, "AGENTS.md");

        // Install for vscode (maps to copilot instructions)
        var vscodeExit = SkillCommand.Execute(_root, new Options(["--agent", "vscode"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, vscodeExit);
        Assert.True(File.Exists(copilotInstructions));

        // Install for rider (maps to AGENTS.md)
        var riderExit = SkillCommand.Execute(_root, new Options(["--agent", "rider"]), SkillMode.Install);
        Assert.Equal(Exit.Ok, riderExit);
        Assert.True(File.Exists(agentsMd));

        // Uninstall
        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "vscode"]), SkillMode.Uninstall));
        Assert.False(File.Exists(copilotInstructions));

        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "rider"]), SkillMode.Uninstall));
        Assert.False(File.Exists(agentsMd));
    }

    /// <summary>
    /// install rewrites a file the user also owns, so it adopts that file's line endings instead of
    /// imposing its own. Converting a CRLF rules file to LF on the first install is a full-file diff
    /// the user never made.
    /// </summary>
    [Fact]
    public void Install_keeps_the_line_endings_the_target_file_already_uses()
    {
        var agentsMd = Path.Combine(_root, "AGENTS.md");

        File.WriteAllText(agentsMd, "# Rules\r\n\r\nKeep production up.\r\n");
        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "codex"]), SkillMode.Install));
        AssertNoBareLf(File.ReadAllText(agentsMd));

        File.WriteAllText(agentsMd, "# Rules\n\nKeep production up.\n");
        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "codex"]), SkillMode.Install));
        Assert.DoesNotContain('\r', File.ReadAllText(agentsMd));
    }

    private static void AssertNoBareLf(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            Assert.True(i > 0 && text[i - 1] == '\r', $"bare LF at index {i}");
        }
    }

    /// <summary>
    /// An --agent all run aims several agents at the same AGENTS.md. Without dedup each rewrites the
    /// file and prints its own line, so one install reads as a repeated failure. Today mimo, codex
    /// and opencode all resolve to the repository AGENTS.md, which is the one path that collapses.
    /// </summary>
    [Fact]
    public void Each_target_path_is_written_once_even_when_several_agents_share_it()
    {
        var agentsMd = Path.Combine(_root, "AGENTS.md");
        File.WriteAllText(agentsMd, "# Rules\n");

        using var sw = new StringWriter();
        var origOut = Console.Out;
        try
        {
            Console.SetOut(sw);
            Assert.Equal(Exit.Ok, SkillCommand.Install(_root, "all", isGlobal: false));
        }
        finally
        {
            Console.SetOut(origOut);
        }

        var writes = sw.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("wrote ", StringComparison.Ordinal)
                        || l.StartsWith("updated ", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(' ') + 1)..].Trim())
            .ToList();

        Assert.Equal(writes.Count, writes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(1, writes.Count(p => p.Equals(agentsMd, StringComparison.OrdinalIgnoreCase)));

        var content = File.ReadAllText(agentsMd);
        Assert.Equal(1, content.Split(SkillBlock.StartTag).Length - 1);
        Assert.Equal(1, content.Split(SkillBlock.EndTag).Length - 1);
    }

    /// <summary>
    /// Pins the bytes install writes, so extracting the wrapper into <see cref="SkillBlock.Render"/>
    /// cannot move a marker, a newline or the trailing line ending unnoticed. A refactor that is
    /// meant to change no output is the kind that changes output.
    /// </summary>
    [Fact]
    public void InstalledBlockIsByteIdenticalToTheRenderedRules()
    {
        var agentsMd = Path.Combine(_root, "AGENTS.md");

        Assert.Equal(Exit.Ok, SkillCommand.Execute(_root, new Options(["--agent", "codex"]), SkillMode.Install));

        var expected = System.Text.Encoding.UTF8.GetBytes(
            "<!-- csmesh-instructions -->\n"
            + SkillText.Rules.Trim()
            + "\n<!-- /csmesh-instructions -->\n");

        Assert.Equal(expected, File.ReadAllBytes(agentsMd));
    }
}
