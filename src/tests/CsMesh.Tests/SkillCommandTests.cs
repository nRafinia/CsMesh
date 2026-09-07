using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

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
}
