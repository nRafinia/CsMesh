using System.Collections.Frozen;
using CsMesh.Common;
using CsMesh.Skill;

namespace CsMesh.Commands;

public enum SkillMode
{
    Skill,
    Install,
    Uninstall
}

public static class SkillCommand
{
    public static string GetHomeDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetEnvironmentVariable("HOME")
                   ?? ".";
        }
        return home;
    }

    public static IEnumerable<string> SkillTargets(string root)
    {
        yield return Path.Combine(root, ".agents", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(root, ".agents", "rules", "csmesh.md");
        yield return Path.Combine(root, ".claude", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(root, ".cursor", "rules", "csmesh.mdc");
        yield return Path.Combine(root, ".clinerules");
        yield return Path.Combine(root, ".clinerules", "csmesh.md");
        yield return Path.Combine(root, ".windsurfrules");
        yield return Path.Combine(root, ".github", "copilot-instructions.md");
        yield return Path.Combine(root, ".kilocode", "rules", "csmesh.md");
        yield return Path.Combine(root, ".mimocode", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(root, ".opencode", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(root, ".opencode", "rules", "csmesh.md");
        yield return Path.Combine(root, ".opencode", "commands", "csmesh.md");
        yield return Path.Combine(root, ".opencode", "opencode.json");
        yield return Path.Combine(root, "AGENTS.md");
        yield return Path.Combine(root, "GEMINI.md");
    }

    public static IEnumerable<string> GlobalSkillTargets(string home)
    {
        var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude");
        var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(home, ".copilot");
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex");

        yield return Path.Combine(claudeHome, "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(claudeHome, "CLAUDE.md");
        yield return Path.Combine(home, ".cursor", "rules", "csmesh.mdc");
        yield return Path.Combine(home, ".gemini", "config", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(home, ".gemini", "config", "rules", "csmesh.md");
        yield return Path.Combine(copilotHome, "copilot-instructions.md");
        yield return Path.Combine(codexHome, "AGENTS.md");
        yield return Path.Combine(home, ".gemini", "GEMINI.md");
        yield return Path.Combine(home, ".kilocode", "rules", "csmesh.md");
        yield return Path.Combine(home, ".mimocode", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(home, ".mimo", "instructions.md");
        yield return Path.Combine(home, ".cline", "rules", "csmesh.md");
        yield return Path.Combine(home, ".codeium", "windsurf", "memories", "global_rules.md");
        var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        yield return Path.Combine(configDir, "opencode", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(configDir, "opencode", "AGENTS.md");
        yield return Path.Combine(configDir, "opencode", "commands", "csmesh.md");
        yield return Path.Combine(configDir, "opencode", "opencode.json");
        yield return Path.Combine(home, ".opencode", "skills", "csmesh", "SKILL.md");
        yield return Path.Combine(home, ".opencode", "rules", "csmesh.md");
        yield return Path.Combine(home, ".opencode", "commands", "csmesh.md");
    }

    /// <summary>
    /// The files, for one install scope, that receive the delimited rules block rather than a
    /// dedicated skill file. doctor compares exactly these: a dedicated file has no markers to
    /// extract and is reported as installed, not stale.
    ///
    /// Kept beside the Install methods it mirrors. A target added there without one here is then a
    /// missing line in a list a reader can compare against those methods, not a silent hole in the
    /// drift check.
    /// </summary>
    public static IEnumerable<string> BlockTargets(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(basePath, ".claude");
            var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(basePath, ".copilot");
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(basePath, ".codex");
            var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(basePath, ".config");

            yield return Path.Combine(claudeHome, "CLAUDE.md");
            yield return Path.Combine(basePath, ".codeium", "windsurf", "memories", "global_rules.md");
            yield return Path.Combine(copilotHome, "copilot-instructions.md");
            yield return Path.Combine(basePath, ".mimo", "instructions.md");
            yield return Path.Combine(codexHome, "AGENTS.md");
            yield return Path.Combine(basePath, ".gemini", "GEMINI.md");
            yield return Path.Combine(configDir, "opencode", "AGENTS.md");
        }
        else
        {
            yield return Path.Combine(basePath, ".windsurfrules");
            yield return Path.Combine(basePath, ".clinerules");
            yield return Path.Combine(basePath, ".github", "copilot-instructions.md");
            yield return Path.Combine(basePath, "AGENTS.md");
            yield return Path.Combine(basePath, "GEMINI.md");
        }
    }

    public static int Execute(string root, Options opt, SkillMode mode = SkillMode.Skill)
    {
        var helpRequested = opt.Flag("help") || opt.Flag("h") || opt.Positional.Contains("help");
        if (helpRequested)
        {
            return HelpCommand.Show(mode switch
            {
                SkillMode.Install => "install",
                SkillMode.Uninstall => "uninstall",
                _ => "skill"
            });
        }

        var hasInstallFlag = opt.Flag("install") || opt.Flag("i");
        var hasUninstallFlag = opt.Flag("uninstall") || opt.Flag("u");

        if (hasInstallFlag && hasUninstallFlag)
        {
            Console.Error.WriteLine("csmesh: cannot specify both --install and --uninstall.");
            return Exit.Usage;
        }

        bool isInstall;
        bool isUninstall;

        if (mode == SkillMode.Install)
        {
            if (hasUninstallFlag)
            {
                Console.Error.WriteLine("csmesh: cannot specify --uninstall with install command.");
                return Exit.Usage;
            }
            isInstall = true;
            isUninstall = false;
        }
        else if (mode == SkillMode.Uninstall)
        {
            if (hasInstallFlag)
            {
                Console.Error.WriteLine("csmesh: cannot specify --install with uninstall command.");
                return Exit.Usage;
            }
            isInstall = false;
            isUninstall = true;
        }
        else // SkillMode.Skill
        {
            var wantsShow = opt.Flag("show") || opt.Flag("print") || opt.Flag("cat") || opt.Flag("markdown");
            if (wantsShow)
            {
                Console.WriteLine(SkillText.Markdown);
                return Exit.Ok;
            }

            if (hasUninstallFlag)
            {
                isInstall = false;
                isUninstall = true;
            }
            else if (hasInstallFlag)
            {
                isInstall = true;
                isUninstall = false;
            }
            else
            {
                return HelpCommand.Show("skill");
            }
        }

        var isGlobal = opt.Flag("global") || opt.Flag("g");
        var targetAgent = opt.Get("agent", "all").ToLowerInvariant();
        var basePath = isGlobal ? GetHomeDir() : root;

        var wantsMcp = opt.Flag("mcp");
        var wantsSkill = opt.Flag("skill");
        var wantsAll = opt.Flag("all");

        if (isInstall)
        {
            bool doInstallSkills;
            bool doInstallMcp;

            if (mode == SkillMode.Skill)
            {
                doInstallSkills = true;
                doInstallMcp = wantsMcp || wantsAll;
            }
            else
            {
                if (wantsAll || (wantsMcp && wantsSkill))
                {
                    doInstallSkills = true;
                    doInstallMcp = true;
                }
                else if (wantsMcp)
                {
                    doInstallSkills = false;
                    doInstallMcp = true;
                }
                else
                {
                    doInstallSkills = true;
                    doInstallMcp = false;
                }
            }

            if (isGlobal)
            {
                Console.WriteLine($"Installing csmesh globally to user config: {basePath}\n");
            }

            if (doInstallSkills)
            {
                var skillExit = Install(basePath, targetAgent, isGlobal);
                if (skillExit != Exit.Ok) return skillExit;
            }

            if (doInstallMcp)
            {
                Integrate(basePath, root, isGlobal, remove: false);
            }

            return Exit.Ok;
        }

        if (isUninstall)
        {
            bool doUninstallSkills;
            bool doUninstallMcp;

            if (mode == SkillMode.Skill)
            {
                doUninstallSkills = true;
                doUninstallMcp = wantsMcp || wantsAll;
            }
            else
            {
                if (wantsAll || (wantsMcp && wantsSkill))
                {
                    doUninstallSkills = true;
                    doUninstallMcp = true;
                }
                else if (wantsMcp)
                {
                    doUninstallSkills = false;
                    doUninstallMcp = true;
                }
                else
                {
                    doUninstallSkills = true;
                    doUninstallMcp = false;
                }
            }

            if (isGlobal)
            {
                Console.WriteLine($"Uninstalling csmesh globally from user config: {basePath}\n");
            }

            if (doUninstallSkills)
            {
                var skillExit = Uninstall(basePath, targetAgent, isGlobal);
                if (skillExit != Exit.Ok) return skillExit;
            }

            if (doUninstallMcp)
            {
                Integrate(basePath, root, isGlobal, remove: true);
            }

            return Exit.Ok;
        }

        return Exit.Ok;
    }

    private static readonly FrozenSet<string> ValidAgents = new[]
    {
        "claude", "cursor", "vscode", "rider", "windsurf", "cline", "roo", "antigravity",
        "copilot", "kilocode", "mimo", "mimocode", "codex", "kimi", "gemini", "opencode", "all"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the skill and rule files for one scope. Internal rather than private so a test can run
    /// the real write path and compare the files that carry a block against
    /// <see cref="BlockTargets"/> -- the list doctor reads. The two are maintained separately, and
    /// the failure this guards against is a block target added here and missing there, which leaves
    /// doctor silently blind to exactly the file that needed checking.
    /// </summary>
    internal static int Install(string basePath, string targetAgent, bool isGlobal)
    {
        if (!ValidAgents.Contains(targetAgent))
        {
            Console.Error.WriteLine($"Unknown agent target '{targetAgent}'. Supported targets: claude, cursor, vscode, rider, windsurf, cline, antigravity, copilot, kilocode, mimo, codex, gemini, opencode, all.");
            return Exit.Usage;
        }

        // One write per path per install. Several agents in an --agent all run target the same
        // AGENTS.md, and without this each rewrites it and prints its own line. The comparer is
        // case-insensitive on Windows, where two spellings of one path name one file.
        var written = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var actions = new Dictionary<string, Action>
        {
            ["claude"] = () => InstallClaude(basePath, isGlobal, written),
            ["cursor"] = () => InstallCursor(basePath, written),
            ["antigravity"] = () => InstallAntigravity(basePath, isGlobal, written),
            ["windsurf"] = () => InstallWindsurf(basePath, isGlobal, written),
            ["cline"] = () => InstallCline(basePath, isGlobal, written),
            ["copilot"] = () => InstallCopilot(basePath, isGlobal, written),
            ["kilocode"] = () => InstallKilocode(basePath, written),
            ["mimo"] = () => InstallMimo(basePath, isGlobal, written),
            ["codex"] = () => InstallCodex(basePath, isGlobal, written),
            ["gemini"] = () => InstallGemini(basePath, isGlobal, written),
            ["opencode"] = () => InstallOpencode(basePath, isGlobal, written)
        };

        if (targetAgent is "all")
        {
            foreach (var action in actions.Values) action();
        }
        else
        {
            var normalized = targetAgent switch
            {
                "roo" => "cline",
                "mimocode" => "mimo",
                "kimi" => "codex",
                "vscode" => "copilot",
                "rider" => "codex",
                _ => targetAgent
            };
            actions[normalized]();
        }

        return Exit.Ok;
    }

    /// <summary>
    /// Registers the MCP server and the grep hook, or takes them back out.
    ///
    /// Both edit files the user owns and shares with other tools, so both merge rather than
    /// overwrite and both are reversible. Anything installed that cannot be uninstalled leaves a
    /// dead entry behind the day csmesh is removed, pointing at a binary that is no longer there.
    /// </summary>
    private static void Integrate(string basePath, string repoRoot, bool isGlobal, bool remove)
    {
        var claudeDir = isGlobal
            ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(basePath, ".claude")
            : Path.Combine(repoRoot, ".claude");

        var targets = isGlobal
            ? AgentIntegration.GlobalServerTargets(basePath)
            : AgentIntegration.ProjectServerTargets(repoRoot);

        // A machine-wide registration must not name a repository, or every other project's
        // questions get answered from this one's graph.
        string? pinned = isGlobal ? null : repoRoot;

        var geminiSettings = isGlobal
            ? Path.Combine(basePath, ".gemini", "settings.json")
            : Path.Combine(repoRoot, ".gemini", "settings.json");

        var antigravityHooks = isGlobal
            ? Path.Combine(basePath, ".gemini", "config", "hooks.json")
            : Path.Combine(repoRoot, ".agents", "hooks.json");

        Console.WriteLine();

        if (remove)
        {
            foreach (var target in targets)
            {
                if (AgentIntegration.UnregisterServer(target, out var gone))
                {
                    Console.WriteLine($"  mcp server   {gone} from {target}");
                }
            }

            var opencodeTargets = isGlobal
                ? AgentIntegration.GlobalOpencodeTargets(basePath)
                : AgentIntegration.ProjectOpencodeTargets(repoRoot);

            foreach (var target in opencodeTargets)
            {
                if (AgentIntegration.UnregisterOpencodeServer(target, out var gone) && gone != "absent")
                {
                    Console.WriteLine($"  mcp server   {gone} from {target}");
                }
            }

            Console.WriteLine(AgentIntegration.UninstallHook(claudeDir, out var hookGone)
                ? $"  grep hook    {hookGone}"
                : "  grep hook    nothing to remove");

            if (AgentIntegration.UninstallGeminiHook(geminiSettings, out var geminiGone))
            {
                Console.WriteLine($"  gemini hook  {geminiGone}");
            }

            if (AgentIntegration.UninstallAntigravityHook(antigravityHooks, out var agyGone))
            {
                Console.WriteLine($"  antigravity  {agyGone}");
            }

            return;
        }

        foreach (var target in targets)
        {
            // Only global config files that already exist are touched. Creating every one of them
            // would leave configuration for clients the user has not installed, which is litter
            // rather than help; a project file is different, since the repository is the point.
            if (isGlobal && !File.Exists(target))
            {
                var dir = Path.GetDirectoryName(target);
                var isClientInstalled = !string.IsNullOrEmpty(dir) &&
                                        !dir.Equals(basePath, StringComparison.OrdinalIgnoreCase) &&
                                        (Directory.Exists(dir) || (target.Contains(".cline") && Directory.Exists(Path.Combine(basePath, ".cline"))));
                if (!isClientInstalled) continue;
            }

            Console.WriteLine(AgentIntegration.RegisterServer(target, pinned, out var outcome)
                ? $"  mcp server   {outcome} in {target}"
                : $"  mcp server   FAILED {target}: {outcome}");
        }

        var opencodeTargetsToInstall = isGlobal
            ? AgentIntegration.GlobalOpencodeTargets(basePath)
            : AgentIntegration.ProjectOpencodeTargets(repoRoot);

        foreach (var target in opencodeTargetsToInstall)
        {
            if (isGlobal && !File.Exists(target))
            {
                var dir = Path.GetDirectoryName(target);
                var isClientInstalled = !string.IsNullOrEmpty(dir) &&
                                        !dir.Equals(basePath, StringComparison.OrdinalIgnoreCase) &&
                                        Directory.Exists(dir);
                if (!isClientInstalled) continue;
            }

            Console.WriteLine(AgentIntegration.RegisterOpencodeServer(target, pinned, out var outcome)
                ? $"  mcp server   {outcome} in {target}"
                : $"  mcp server   FAILED {target}: {outcome}");
        }

        Console.WriteLine(AgentIntegration.InstallHook(claudeDir, out var hookOutcome)
            ? $"  grep hook    {hookOutcome}"
            : $"  grep hook    FAILED: {hookOutcome}");

        if (File.Exists(geminiSettings) || !isGlobal)
        {
            Console.WriteLine(AgentIntegration.InstallGeminiHook(geminiSettings, out var geminiOutcome)
                ? $"  gemini hook  {geminiOutcome} in {geminiSettings}"
                : $"  gemini hook  FAILED: {geminiOutcome}");
        }

        if (File.Exists(antigravityHooks) || !isGlobal)
        {
            Console.WriteLine(AgentIntegration.InstallAntigravityHook(antigravityHooks, out var agyOutcome)
                ? $"  antigravity  {agyOutcome} in {antigravityHooks}"
                : $"  antigravity  FAILED: {agyOutcome}");
        }

        Console.WriteLine($"  binary       {AgentIntegration.BinaryPath()}");

        if (isGlobal)
        {
            Console.WriteLine("  scope        no --repo recorded; serve resolves the repository from where the client starts it");
        }

        Console.WriteLine();

        // Worth more than one line, because the failure it prevents is expensive and does not
        // look like a failure. A client starts its MCP servers when it opens, so one that was
        // already running started before this config existed -- with its own working directory,
        // which is the IDE's install folder. It will not re-read the file, so it stays pointed
        // there until the client is restarted, and every answer comes back empty as though the
        // repository had nothing in it.
        Console.WriteLine("  RESTART YOUR CLIENT before using csmesh through MCP.");
        Console.WriteLine("  A server it already started is still pointed at its own directory and");
        Console.WriteLine("  will not re-read this config. Until then, the CLI works normally.");
        Console.WriteLine();
        Console.WriteLine("  Remove with: csmesh uninstall --mcp");
    }

    private static void InstallClaude(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(basePath, ".claude");
            WriteFile(Path.Combine(claudeHome, "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(claudeHome, "CLAUDE.md"), written, SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".claude", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
        }
    }

    private static void InstallCursor(string basePath, ISet<string> written)
    {
        var mdcPath = Path.Combine(basePath, ".cursor", "rules", "csmesh.mdc");
        WriteFile(mdcPath, written, SkillText.CursorMdc);

        var oldMd = Path.Combine(basePath, ".cursor", "rules", "csmesh.md");
        if (File.Exists(oldMd))
        {
            try { File.Delete(oldMd); } catch { }
        }
    }

    private static void InstallAntigravity(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            WriteFile(Path.Combine(basePath, ".gemini", "config", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".gemini", "config", "rules", "csmesh.md"), written, SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".agents", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".agents", "rules", "csmesh.md"), written, SkillText.Rules);
        }
    }

    private static void InstallWindsurf(string basePath, bool isGlobal, ISet<string> written)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".codeium", "windsurf", "memories", "global_rules.md")
            : Path.Combine(basePath, ".windsurfrules");
        WriteOrUpdateBlock(path, written, SkillText.Rules);
    }

    private static void InstallCline(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            var docs = Path.Combine(basePath, "Documents", "Cline", "Rules");
            var dir = Directory.Exists(docs) ? docs : Path.Combine(basePath, ".cline", "rules");
            WriteFile(Path.Combine(dir, "csmesh.md"), written, SkillText.Rules);
        }
        else
        {
            var clineDir = Path.Combine(basePath, ".clinerules");
            if (Directory.Exists(clineDir))
            {
                WriteFile(Path.Combine(clineDir, "csmesh.md"), written, SkillText.Rules);
            }
            else
            {
                WriteOrUpdateBlock(clineDir, written, SkillText.Rules);
            }
        }
    }

    private static void InstallCopilot(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(basePath, ".copilot");
            WriteOrUpdateBlock(Path.Combine(copilotHome, "copilot-instructions.md"), written, SkillText.Rules);
        }
        else
        {
            WriteOrUpdateBlock(Path.Combine(basePath, ".github", "copilot-instructions.md"), written, SkillText.Rules);
        }
    }

    private static void InstallKilocode(string basePath, ISet<string> written)
    {
        WriteFile(Path.Combine(basePath, ".kilocode", "rules", "csmesh.md"), written, SkillText.Rules);
    }

    private static void InstallMimo(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            WriteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(basePath, ".mimo", "instructions.md"), written, SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(basePath, "AGENTS.md"), written, SkillText.Rules);
        }
    }

    private static void InstallCodex(string basePath, bool isGlobal, ISet<string> written)
    {
        var path = isGlobal
            ? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(basePath, ".codex"), "AGENTS.md")
            : Path.Combine(basePath, "AGENTS.md");
        WriteOrUpdateBlock(path, written, SkillText.Rules);
    }

    private static void InstallGemini(string basePath, bool isGlobal, ISet<string> written)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".gemini", "GEMINI.md")
            : Path.Combine(basePath, "GEMINI.md");
        WriteOrUpdateBlock(path, written, SkillText.Rules);
    }

    private const string OpencodeCommandContent = """
        ---
        description: C# structural code intelligence with csmesh (where, trace, impl, blast-radius, context)
        ---
        Use the csmesh structural C# intelligence tool to analyze and query the code:
        $ARGUMENTS
        """;

    private static void InstallOpencode(string basePath, bool isGlobal, ISet<string> written)
    {
        if (isGlobal)
        {
            var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(basePath, ".config");
            WriteOrUpdateBlock(Path.Combine(configDir, "opencode", "AGENTS.md"), written, SkillText.Rules);
            WriteFile(Path.Combine(configDir, "opencode", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".opencode", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"), written, SkillText.Rules);
            WriteFile(Path.Combine(configDir, "opencode", "commands", "csmesh.md"), written, OpencodeCommandContent);
            WriteFile(Path.Combine(basePath, ".opencode", "commands", "csmesh.md"), written, OpencodeCommandContent);

            var opencodeConfig = Path.Combine(configDir, "opencode", "opencode.json");
            AgentIntegration.RegisterOpencodeServer(opencodeConfig, repoRoot: null, out var outcome);
            Console.WriteLine($"  mcp server   {outcome} in {opencodeConfig}");
        }
        else
        {
            WriteOrUpdateBlock(Path.Combine(basePath, "AGENTS.md"), written, SkillText.Rules);
            WriteFile(Path.Combine(basePath, ".opencode", "skills", "csmesh", "SKILL.md"), written, SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"), written, SkillText.Rules);
            WriteFile(Path.Combine(basePath, ".opencode", "commands", "csmesh.md"), written, OpencodeCommandContent);

            var opencodeConfig = Path.Combine(basePath, ".opencode", "opencode.json");
            AgentIntegration.RegisterOpencodeServer(opencodeConfig, repoRoot: basePath, out var outcome);
            Console.WriteLine($"  mcp server   {outcome} in {opencodeConfig}");
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> once per install. A path already written by an earlier
    /// agent in the same run is skipped: the second writer would produce the same bytes (every
    /// shared file carries <see cref="SkillText.Rules"/>), and the point of skipping is the line it
    /// does not print and the rewrite it does not do.
    /// </summary>
    private static void WriteFile(string path, ISet<string> written, string content)
    {
        if (!written.Add(Path.GetFullPath(path))) return;
        WriteFileCore(path, content);
    }

    private static void WriteFileCore(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, content);
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>
    /// The line ending the file already uses, so rewriting it does not convert its whole body. A
    /// CRLF working tree on Windows turned into LF by the first install is a full-file diff no one
    /// asked for.
    /// </summary>
    private static string NewlineOf(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// Rewrites every line ending in <paramref name="text"/> to <paramref name="newline"/>. The body
    /// of the block comes from <see cref="SkillText"/>, whose line endings follow the checkout, so
    /// without this the wrapper and the body could disagree inside one file.
    /// </summary>
    private static string WithNewline(string text, string newline) =>
        text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newline);

    private static void WriteOrUpdateBlock(string filePath, ISet<string> written, string blockContent)
    {
        const string startTag = SkillBlock.StartTag;
        const string endTag = SkillBlock.EndTag;

        if (!written.Add(Path.GetFullPath(filePath))) return;

        var wrappedBlock = SkillBlock.Render(blockContent);

        if (!File.Exists(filePath))
        {
            // A new file keeps today's bytes: the rendered block verbatim plus one trailing newline.
            // Its endings are the ones the checkout gives SkillText -- LF around the markers, and
            // whatever the source uses inside the body -- because there is no file yet to read them
            // from. A rewrite is the case that follows the target.
            WriteFileCore(filePath, wrappedBlock + "\n");
            return;
        }

        var existing = File.ReadAllText(filePath);
        var newline = NewlineOf(existing);
        var startIndex = existing.IndexOf(startTag, StringComparison.Ordinal);
        var endIndex = existing.IndexOf(endTag, StringComparison.Ordinal);

        if (startIndex >= 0 && endIndex > startIndex)
        {
            var before = existing[..startIndex].TrimEnd();
            var after = existing[(endIndex + endTag.Length)..].TrimStart();
            var updated = string.IsNullOrEmpty(before)
                ? (string.IsNullOrEmpty(after) ? wrappedBlock : $"{wrappedBlock}\n\n{after}")
                : (string.IsNullOrEmpty(after) ? $"{before}\n\n{wrappedBlock}" : $"{before}\n\n{wrappedBlock}\n\n{after}");

            File.WriteAllText(filePath, WithNewline(updated + "\n", newline));
            Console.WriteLine($"updated {filePath}");
        }
        else
        {
            var updated = existing.TrimEnd() + "\n\n" + wrappedBlock + "\n";
            File.WriteAllText(filePath, WithNewline(updated, newline));
            Console.WriteLine($"updated {filePath}");
        }
    }

    private static int Uninstall(string basePath, string targetAgent, bool isGlobal)
    {
        if (!ValidAgents.Contains(targetAgent))
        {
            Console.Error.WriteLine($"Unknown agent target '{targetAgent}'. Supported targets: claude, cursor, vscode, rider, windsurf, cline, antigravity, copilot, kilocode, mimo, codex, gemini, opencode, all.");
            return Exit.Usage;
        }

        var actions = new Dictionary<string, Action>
        {
            ["claude"] = () => UninstallClaude(basePath, isGlobal),
            ["cursor"] = () => UninstallCursor(basePath),
            ["antigravity"] = () => UninstallAntigravity(basePath, isGlobal),
            ["windsurf"] = () => UninstallWindsurf(basePath, isGlobal),
            ["cline"] = () => UninstallCline(basePath, isGlobal),
            ["copilot"] = () => UninstallCopilot(basePath, isGlobal),
            ["kilocode"] = () => UninstallKilocode(basePath),
            ["mimo"] = () => UninstallMimo(basePath, isGlobal),
            ["codex"] = () => UninstallCodex(basePath, isGlobal),
            ["gemini"] = () => UninstallGemini(basePath, isGlobal),
            ["opencode"] = () => UninstallOpencode(basePath, isGlobal)
        };

        if (targetAgent is "all")
        {
            foreach (var action in actions.Values) action();
        }
        else
        {
            var normalized = targetAgent switch
            {
                "roo" => "cline",
                "mimocode" => "mimo",
                "kimi" => "codex",
                "vscode" => "copilot",
                "rider" => "codex",
                _ => targetAgent
            };
            actions[normalized]();
        }

        return Exit.Ok;
    }

    private static void UninstallClaude(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(basePath, ".claude");
            DeleteFile(Path.Combine(claudeHome, "skills", "csmesh", "SKILL.md"));
            RemoveBlock(Path.Combine(claudeHome, "CLAUDE.md"));
        }
        else
        {
            DeleteFile(Path.Combine(basePath, ".claude", "skills", "csmesh", "SKILL.md"));
        }
    }

    private static void UninstallCursor(string basePath)
    {
        DeleteFile(Path.Combine(basePath, ".cursor", "rules", "csmesh.mdc"));
        DeleteFile(Path.Combine(basePath, ".cursor", "rules", "csmesh.md"));
    }

    private static void UninstallAntigravity(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            DeleteFile(Path.Combine(basePath, ".gemini", "config", "skills", "csmesh", "SKILL.md"));
            DeleteFile(Path.Combine(basePath, ".gemini", "config", "rules", "csmesh.md"));
        }
        else
        {
            DeleteFile(Path.Combine(basePath, ".agents", "skills", "csmesh", "SKILL.md"));
            DeleteFile(Path.Combine(basePath, ".agents", "rules", "csmesh.md"));
        }
    }

    private static void UninstallWindsurf(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".codeium", "windsurf", "memories", "global_rules.md")
            : Path.Combine(basePath, ".windsurfrules");
        RemoveBlock(path);
    }

    private static void UninstallCline(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var docs = Path.Combine(basePath, "Documents", "Cline", "Rules");
            var dir = Directory.Exists(docs) ? docs : Path.Combine(basePath, ".cline", "rules");
            DeleteFile(Path.Combine(dir, "csmesh.md"));
        }
        else
        {
            DeleteFile(Path.Combine(basePath, ".clinerules", "csmesh.md"));
            RemoveBlock(Path.Combine(basePath, ".clinerules"));
        }
    }

    private static void UninstallCopilot(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(basePath, ".copilot");
            RemoveBlock(Path.Combine(copilotHome, "copilot-instructions.md"));
        }
        else
        {
            RemoveBlock(Path.Combine(basePath, ".github", "copilot-instructions.md"));
        }
    }

    private static void UninstallKilocode(string basePath)
    {
        DeleteFile(Path.Combine(basePath, ".kilocode", "rules", "csmesh.md"));
    }

    private static void UninstallMimo(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            DeleteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"));
            RemoveBlock(Path.Combine(basePath, ".mimo", "instructions.md"));
        }
        else
        {
            DeleteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"));
            RemoveBlock(Path.Combine(basePath, "AGENTS.md"));
        }
    }

    private static void UninstallCodex(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(basePath, ".codex"), "AGENTS.md")
            : Path.Combine(basePath, "AGENTS.md");
        RemoveBlock(path);
    }

    private static void UninstallGemini(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".gemini", "GEMINI.md")
            : Path.Combine(basePath, "GEMINI.md");
        RemoveBlock(path);
    }

    private static void UninstallOpencode(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(basePath, ".config");
            RemoveBlock(Path.Combine(configDir, "opencode", "AGENTS.md"));
            DeleteFile(Path.Combine(configDir, "opencode", "skills", "csmesh", "SKILL.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "skills", "csmesh", "SKILL.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"));
            DeleteFile(Path.Combine(configDir, "opencode", "commands", "csmesh.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "commands", "csmesh.md"));

            var opencodeConfig = Path.Combine(configDir, "opencode", "opencode.json");
            if (AgentIntegration.UnregisterOpencodeServer(opencodeConfig, out var outcome) && outcome != "absent")
            {
                Console.WriteLine($"  mcp server   {outcome} from {opencodeConfig}");
            }
        }
        else
        {
            RemoveBlock(Path.Combine(basePath, "AGENTS.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "skills", "csmesh", "SKILL.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "commands", "csmesh.md"));

            var opencodeConfig = Path.Combine(basePath, ".opencode", "opencode.json");
            if (AgentIntegration.UnregisterOpencodeServer(opencodeConfig, out var outcome) && outcome != "absent")
            {
                Console.WriteLine($"  mcp server   {outcome} from {opencodeConfig}");
            }
            var rootConfig = Path.Combine(basePath, "opencode.json");
            if (AgentIntegration.UnregisterOpencodeServer(rootConfig, out var rootOutcome) && rootOutcome != "absent")
            {
                Console.WriteLine($"  mcp server   {rootOutcome} from {rootConfig}");
            }
        }
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
            Console.WriteLine($"deleted {path}");

            // Clean up empty directory if applicable
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                try { Directory.Delete(dir); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed to delete {path}: {ex.Message}");
        }
    }

    private static void RemoveBlock(string filePath)
    {
        const string startTag = SkillBlock.StartTag;
        const string endTag = SkillBlock.EndTag;

        if (!File.Exists(filePath)) return;

        try
        {
            var existing = File.ReadAllText(filePath);
            var startIndex = existing.IndexOf(startTag, StringComparison.Ordinal);
            var endIndex = existing.IndexOf(endTag, StringComparison.Ordinal);

            if (startIndex >= 0 && endIndex > startIndex)
            {
                var before = existing[..startIndex].TrimEnd();
                var after = existing[(endIndex + endTag.Length)..].TrimStart();

                var remaining = string.IsNullOrEmpty(before)
                    ? after
                    : (string.IsNullOrEmpty(after) ? before : $"{before}\n\n{after}");

                if (string.IsNullOrWhiteSpace(remaining))
                {
                    File.Delete(filePath);
                    Console.WriteLine($"deleted {filePath}");
                }
                else
                {
                    File.WriteAllText(filePath, remaining.TrimEnd() + "\n");
                    Console.WriteLine($"updated {filePath}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"failed to update {filePath}: {ex.Message}");
        }
    }
}
