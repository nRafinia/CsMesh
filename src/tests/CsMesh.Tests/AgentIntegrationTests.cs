using System.Text.Json;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// These write into files the user owns and shares with every other tool they have installed. The
/// damage from getting it wrong is not a broken csmesh -- it is somebody's other MCP servers
/// disappearing, or their own hooks, silently, because they installed a skill file.
/// </summary>
public sealed class AgentIntegrationTests : IDisposable
{
    private readonly string _root;

    public AgentIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-integration-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    private string Mcp => Path.Combine(_root, ".mcp.json");
    private string ClaudeDir => Path.Combine(_root, ".claude");

    private JsonElement Read(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    [Fact]
    public void RegistrationWritesAnAbsoluteBinaryPathAndRepo()
    {
        Assert.True(AgentIntegration.RegisterServer(Mcp, _root, out var outcome));
        Assert.Equal("added", outcome);

        var entry = Read(Mcp).GetProperty("mcpServers").GetProperty("csmesh");

        Assert.Equal(Path.GetFullPath(_root), entry.GetProperty("args")[2].GetString());

        // "csmesh" alone only works when it is on PATH, and the machines where it is not are
        // exactly the ones where the client reports a server that would not start and says
        // nothing about why.
        var command = entry.GetProperty("command").GetString()!;
        Assert.True(Path.IsPathRooted(command) || command.StartsWith("csmesh", StringComparison.Ordinal));
    }

    /// <summary>The one that matters: somebody else's servers must survive.</summary>
    [Fact]
    public void RegistrationKeepsOtherServersAndUnrelatedKeys()
    {
        File.WriteAllText(Mcp, """
            {
              "mcpServers": { "other": { "command": "other-tool" } },
              "somethingElse": 42
            }
            """);

        Assert.True(AgentIntegration.RegisterServer(Mcp, _root, out _));

        var root = Read(Mcp);
        Assert.Equal("other-tool",
            root.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString());
        Assert.Equal(42, root.GetProperty("somethingElse").GetInt32());
    }

    [Fact]
    public void RegisteringTwiceUpdatesRatherThanDuplicating()
    {
        AgentIntegration.RegisterServer(Mcp, _root, out _);
        Assert.True(AgentIntegration.RegisterServer(Mcp, _root, out var second));

        Assert.Equal("updated", second);
        Assert.Equal(1, Read(Mcp).GetProperty("mcpServers").EnumerateObject().Count());
    }

    [Fact]
    public void UnregisteringLeavesEverythingElseInPlace()
    {
        File.WriteAllText(Mcp, """{ "mcpServers": { "other": { "command": "other-tool" } } }""");
        AgentIntegration.RegisterServer(Mcp, _root, out _);

        Assert.True(AgentIntegration.UnregisterServer(Mcp, out var outcome));
        Assert.Equal("removed", outcome);

        var servers = Read(Mcp).GetProperty("mcpServers");
        Assert.False(servers.TryGetProperty("csmesh", out _));
        Assert.True(servers.TryGetProperty("other", out _));
    }

    [Fact]
    public void UnregisteringWhatWasNeverThereIsNotAnError()
    {
        Assert.False(AgentIntegration.UnregisterServer(Mcp, out var outcome));
        Assert.Equal("absent", outcome);
    }

    [Fact]
    public void HookInstallWritesAScriptAndRegistersIt()
    {
        Assert.True(AgentIntegration.InstallHook(ClaudeDir, out var scriptPath));
        Assert.True(File.Exists(scriptPath));

        var preToolUse = Read(Path.Combine(ClaudeDir, "settings.json"))
            .GetProperty("hooks").GetProperty("PreToolUse");

        Assert.Equal(1, preToolUse.GetArrayLength());
        Assert.Equal("Grep", preToolUse[0].GetProperty("matcher").GetString());
    }

    /// <summary>
    /// Re-running install must not stack a second copy that fires alongside the first, which is
    /// what an append-only implementation does and what nobody notices until the hint appears
    /// three times.
    /// </summary>
    [Fact]
    public void InstallingTheHookTwiceLeavesOneEntry()
    {
        AgentIntegration.InstallHook(ClaudeDir, out _);
        AgentIntegration.InstallHook(ClaudeDir, out _);

        var preToolUse = Read(Path.Combine(ClaudeDir, "settings.json"))
            .GetProperty("hooks").GetProperty("PreToolUse");

        Assert.Equal(1, preToolUse.GetArrayLength());
    }

    [Fact]
    public void HookInstallKeepsTheUsersOwnHooksAndSettings()
    {
        Directory.CreateDirectory(ClaudeDir);
        File.WriteAllText(Path.Combine(ClaudeDir, "settings.json"), """
            {
              "model": "opus",
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "mine.sh" } ] } ] }
            }
            """);

        AgentIntegration.InstallHook(ClaudeDir, out _);

        var root = Read(Path.Combine(ClaudeDir, "settings.json"));
        Assert.Equal("opus", root.GetProperty("model").GetString());

        var matchers = root.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Select(e => e.GetProperty("matcher").GetString()).ToList();

        Assert.Contains("Bash", matchers);
        Assert.Contains("Grep", matchers);
    }

    [Fact]
    public void UninstallingTheHookRemovesOnlyOurs()
    {
        Directory.CreateDirectory(ClaudeDir);
        File.WriteAllText(Path.Combine(ClaudeDir, "settings.json"), """
            {
              "model": "opus",
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "mine.sh" } ] } ] }
            }
            """);

        AgentIntegration.InstallHook(ClaudeDir, out var scriptPath);
        Assert.True(AgentIntegration.UninstallHook(ClaudeDir, out _));

        Assert.False(File.Exists(scriptPath));

        var root = Read(Path.Combine(ClaudeDir, "settings.json"));
        Assert.Equal("opus", root.GetProperty("model").GetString());

        var matchers = root.GetProperty("hooks").GetProperty("PreToolUse")
            .EnumerateArray().Select(e => e.GetProperty("matcher").GetString()).ToList();

        Assert.Equal(["Bash"], matchers);
    }

    /// <summary>
    /// A config the user hand-edited into invalid JSON must not be rewritten from scratch, which
    /// would throw away whatever they were in the middle of.
    /// </summary>
    [Fact]
    public void AMalformedConfigIsReportedRatherThanReplaced()
    {
        File.WriteAllText(Mcp, "{ this is not json");

        Assert.False(AgentIntegration.RegisterServer(Mcp, _root, out _));
        Assert.Equal("{ this is not json", File.ReadAllText(Mcp));
    }

    /// <summary>
    /// Plain text on stdout is discarded by Claude Code without an error, so a hook that emits it
    /// does nothing while appearing to work. That is what shipped the first time.
    /// </summary>
    [Fact]
    public void BothHookScriptsEmitTheStructuredPayloadShape()
    {
        foreach (var script in new[] { HookText.Bash, HookText.PowerShell })
        {
            Assert.Contains("hookSpecificOutput", script, StringComparison.Ordinal);
            Assert.Contains("additionalContext", script, StringComparison.Ordinal);
            Assert.Contains("PreToolUse", script, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// Registration across the several config files a machine carries, and the one hook format that
/// is not Claude's.
/// </summary>
public sealed class MultiTargetIntegrationTests : IDisposable
{
    private readonly string _home;

    public MultiTargetIntegrationTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "csmesh-multi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_home);
    }

    private JsonElement Read(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    [Fact]
    public void GlobalTargetsCoverTheClientsThatCarryTheirOwnConfig()
    {
        var targets = AgentIntegration.GlobalServerTargets(_home).ToList();

        Assert.Contains(targets, t => t.EndsWith(".claude.json", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains(".cursor", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains(".gemini", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains("claude_desktop_config.json", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains("windsurf", StringComparison.Ordinal));

        // Antigravity has moved this more than once and its surfaces still disagree, so all
        // three are written. Missing the right one is invisible: the server never appears and
        // nothing says why.
        Assert.Contains(targets, t => t.Contains("antigravity", StringComparison.Ordinal));
        Assert.Contains(targets, t => t.Contains("antigravity-cli", StringComparison.Ordinal));

        // No duplicates: registering the same file twice is harmless but the second write would
        // be reported as an update to something this run just added.
        Assert.Equal(targets.Count, targets.Distinct().Count());
    }

    /// <summary>
    /// A machine-wide registration must not name a repository. Pinning one would answer every
    /// other project's questions from the wrong graph -- confidently, and with nothing to show
    /// anything was wrong.
    /// </summary>
    /// <summary>
    /// Antigravity's global and workspace configs are different files. A project install that
    /// wrote only .mcp.json left it with nothing, which is what happened.
    /// </summary>
    [Fact]
    public void ProjectTargetsIncludeAntigravitysWorkspaceConfig()
    {
        var targets = AgentIntegration.ProjectServerTargets(_home).ToList();

        Assert.Contains(targets, t => t.EndsWith(Path.Combine(".agents", "mcp_config.json"), StringComparison.Ordinal));
        Assert.Contains(targets, t => t.EndsWith(Path.Combine(".vscode", "mcp.json"), StringComparison.Ordinal));
        Assert.Contains(targets, t => t.EndsWith(".mcp.json", StringComparison.Ordinal));
        Assert.Equal(targets.Count, targets.Distinct().Count());
    }

    [Fact]
    public void AGlobalRegistrationRecordsNoRepository()
    {
        var path = Path.Combine(_home, "global.json");
        AgentIntegration.RegisterServer(path, repoRoot: null, out _);

        var args = Read(path).GetProperty("mcpServers").GetProperty("csmesh").GetProperty("args");

        Assert.Equal(1, args.GetArrayLength());
        Assert.Equal("serve", args[0].GetString());
    }

    [Fact]
    public void AProjectRegistrationPinsTheRepository()
    {
        var path = Path.Combine(_home, "project.json");
        AgentIntegration.RegisterServer(path, _home, out _);

        var args = Read(path).GetProperty("mcpServers").GetProperty("csmesh").GetProperty("args");

        Assert.Equal(3, args.GetArrayLength());
        Assert.Equal("--repo", args[1].GetString());
    }

    /// <summary>
    /// Antigravity's format is close enough to Claude's to look interchangeable and not close
    /// enough to be: BeforeTool rather than PreToolUse, and an array of strings rather than of
    /// objects. Writing Claude's shape produces a file the IDE accepts and ignores.
    /// </summary>
    [Fact]
    public void TheGeminiHookUsesBeforeToolAndAStringArray()
    {
        var path = Path.Combine(_home, "settings.json");
        Assert.True(AgentIntegration.InstallGeminiHook(path, out _));

        var beforeTool = Read(path).GetProperty("hooks").GetProperty("BeforeTool");

        Assert.Equal(1, beforeTool.GetArrayLength());
        Assert.Contains("grep", beforeTool[0].GetProperty("matcher").GetString()!, StringComparison.Ordinal);

        foreach (var entry in beforeTool[0].GetProperty("hooks").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, entry.ValueKind);
        }
    }

    [Fact]
    public void TheGeminiHookMergesAndDoesNotDuplicate()
    {
        var path = Path.Combine(_home, "settings.json");
        File.WriteAllText(path, """
            {
              "mcpServers": { "other": { "command": "other" } },
              "hooks": { "BeforeTool": [ { "matcher": "web_search", "hooks": [ "theirs" ] } ] }
            }
            """);

        AgentIntegration.InstallGeminiHook(path, out _);
        AgentIntegration.InstallGeminiHook(path, out _);

        var root = Read(path);
        Assert.True(root.GetProperty("mcpServers").TryGetProperty("other", out _));
        Assert.Equal(2, root.GetProperty("hooks").GetProperty("BeforeTool").GetArrayLength());

        Assert.True(AgentIntegration.UninstallGeminiHook(path, out _));

        var after = Read(path).GetProperty("hooks").GetProperty("BeforeTool");
        Assert.Equal(1, after.GetArrayLength());
        Assert.Equal("web_search", after[0].GetProperty("matcher").GetString());
    }

    [Fact]
    public void TheAntigravityHookInstallsAndUninstallsCorrectly()
    {
        var path = Path.Combine(_home, "hooks.json");
        Assert.True(AgentIntegration.InstallAntigravityHook(path, out var outcome));
        Assert.Equal("added", outcome);

        var root = Read(path);
        Assert.True(root.TryGetProperty("csmesh-grep-hint", out var hookDef));
        var preTool = hookDef.GetProperty("PreToolUse");
        Assert.Equal(1, preTool.GetArrayLength());
        Assert.Contains("grep", preTool[0].GetProperty("matcher").GetString()!);

        Assert.True(AgentIntegration.UninstallAntigravityHook(path, out var uninstalled));
        Assert.Equal("removed", uninstalled);

        var afterRoot = Read(path);
        Assert.False(afterRoot.TryGetProperty("csmesh-grep-hint", out _));
    }

    [Fact]
    public void FindRootDiscardsUnexpandedIdeVariables()
    {
        var discovered = RepositoryLocator.FindRoot("${workspaceFolder}");
        Assert.False(discovered.Contains("${workspaceFolder}", StringComparison.Ordinal));
        Assert.True(Directory.Exists(discovered));
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* temp dir */ }
    }
}

