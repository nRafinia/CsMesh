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
        yield return Path.Combine(root, ".opencode", "rules", "csmesh.md");
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
        yield return Path.Combine(configDir, "opencode", "AGENTS.md");
        yield return Path.Combine(home, ".opencode", "rules", "csmesh.md");
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
        "claude", "cursor", "windsurf", "cline", "roo", "antigravity",
        "copilot", "kilocode", "mimo", "mimocode", "codex", "kimi", "gemini", "opencode", "all"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static int Install(string basePath, string targetAgent, bool isGlobal)
    {
        if (!ValidAgents.Contains(targetAgent))
        {
            Console.Error.WriteLine($"Unknown agent target '{targetAgent}'. Supported targets: claude, cursor, windsurf, cline, antigravity, copilot, kilocode, mimo, codex, gemini, opencode, all.");
            return Exit.Usage;
        }

        var actions = new Dictionary<string, Action>
        {
            ["claude"] = () => InstallClaude(basePath, isGlobal),
            ["cursor"] = () => InstallCursor(basePath),
            ["antigravity"] = () => InstallAntigravity(basePath, isGlobal),
            ["windsurf"] = () => InstallWindsurf(basePath, isGlobal),
            ["cline"] = () => InstallCline(basePath, isGlobal),
            ["copilot"] = () => InstallCopilot(basePath, isGlobal),
            ["kilocode"] = () => InstallKilocode(basePath),
            ["mimo"] = () => InstallMimo(basePath, isGlobal),
            ["codex"] = () => InstallCodex(basePath, isGlobal),
            ["gemini"] = () => InstallGemini(basePath, isGlobal),
            ["opencode"] = () => InstallOpencode(basePath, isGlobal)
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
            if (isGlobal && !File.Exists(target)) continue;

            Console.WriteLine(AgentIntegration.RegisterServer(target, pinned, out var outcome)
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

    private static void InstallClaude(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var claudeHome = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(basePath, ".claude");
            WriteFile(Path.Combine(claudeHome, "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(claudeHome, "CLAUDE.md"), SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".claude", "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
        }
    }

    private static void InstallCursor(string basePath)
    {
        var mdcPath = Path.Combine(basePath, ".cursor", "rules", "csmesh.mdc");
        WriteFile(mdcPath, SkillText.CursorMdc);

        var oldMd = Path.Combine(basePath, ".cursor", "rules", "csmesh.md");
        if (File.Exists(oldMd))
        {
            try { File.Delete(oldMd); } catch { }
        }
    }

    private static void InstallAntigravity(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            WriteFile(Path.Combine(basePath, ".gemini", "config", "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".gemini", "config", "rules", "csmesh.md"), SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".agents", "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
            WriteFile(Path.Combine(basePath, ".agents", "rules", "csmesh.md"), SkillText.Rules);
        }
    }

    private static void InstallWindsurf(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".codeium", "windsurf", "memories", "global_rules.md")
            : Path.Combine(basePath, ".windsurfrules");
        WriteOrUpdateBlock(path, SkillText.Rules);
    }

    private static void InstallCline(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var docs = Path.Combine(basePath, "Documents", "Cline", "Rules");
            var dir = Directory.Exists(docs) ? docs : Path.Combine(basePath, ".cline", "rules");
            WriteFile(Path.Combine(dir, "csmesh.md"), SkillText.Rules);
        }
        else
        {
            var clineDir = Path.Combine(basePath, ".clinerules");
            if (Directory.Exists(clineDir))
            {
                WriteFile(Path.Combine(clineDir, "csmesh.md"), SkillText.Rules);
            }
            else
            {
                WriteOrUpdateBlock(clineDir, SkillText.Rules);
            }
        }
    }

    private static void InstallCopilot(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(basePath, ".copilot");
            WriteOrUpdateBlock(Path.Combine(copilotHome, "copilot-instructions.md"), SkillText.Rules);
        }
        else
        {
            WriteOrUpdateBlock(Path.Combine(basePath, ".github", "copilot-instructions.md"), SkillText.Rules);
        }
    }

    private static void InstallKilocode(string basePath)
    {
        WriteFile(Path.Combine(basePath, ".kilocode", "rules", "csmesh.md"), SkillText.Rules);
    }

    private static void InstallMimo(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            WriteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(basePath, ".mimo", "instructions.md"), SkillText.Rules);
        }
        else
        {
            WriteFile(Path.Combine(basePath, ".mimocode", "skills", "csmesh", "SKILL.md"), SkillText.Markdown);
            WriteOrUpdateBlock(Path.Combine(basePath, "AGENTS.md"), SkillText.Rules);
        }
    }

    private static void InstallCodex(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(basePath, ".codex"), "AGENTS.md")
            : Path.Combine(basePath, "AGENTS.md");
        WriteOrUpdateBlock(path, SkillText.Rules);
    }

    private static void InstallGemini(string basePath, bool isGlobal)
    {
        var path = isGlobal
            ? Path.Combine(basePath, ".gemini", "GEMINI.md")
            : Path.Combine(basePath, "GEMINI.md");
        WriteOrUpdateBlock(path, SkillText.Rules);
    }

    private static void InstallOpencode(string basePath, bool isGlobal)
    {
        if (isGlobal)
        {
            var configDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(basePath, ".config");
            WriteOrUpdateBlock(Path.Combine(configDir, "opencode", "AGENTS.md"), SkillText.Rules);
            WriteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"), SkillText.Rules);
        }
        else
        {
            WriteOrUpdateBlock(Path.Combine(basePath, "AGENTS.md"), SkillText.Rules);
            WriteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"), SkillText.Rules);
        }
    }

    private static void WriteFile(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, content);
        Console.WriteLine($"wrote {path}");
    }

    private static void WriteOrUpdateBlock(string filePath, string blockContent)
    {
        const string startTag = "<!-- csmesh-instructions -->";
        const string endTag = "<!-- /csmesh-instructions -->";

        var wrappedBlock = $"{startTag}\n{blockContent.Trim()}\n{endTag}";

        if (!File.Exists(filePath))
        {
            WriteFile(filePath, wrappedBlock + "\n");
            return;
        }

        var existing = File.ReadAllText(filePath);
        var startIndex = existing.IndexOf(startTag, StringComparison.Ordinal);
        var endIndex = existing.IndexOf(endTag, StringComparison.Ordinal);

        if (startIndex >= 0 && endIndex > startIndex)
        {
            var before = existing[..startIndex].TrimEnd();
            var after = existing[(endIndex + endTag.Length)..].TrimStart();
            var updated = string.IsNullOrEmpty(before)
                ? (string.IsNullOrEmpty(after) ? wrappedBlock : $"{wrappedBlock}\n\n{after}")
                : (string.IsNullOrEmpty(after) ? $"{before}\n\n{wrappedBlock}" : $"{before}\n\n{wrappedBlock}\n\n{after}");

            File.WriteAllText(filePath, updated + "\n");
            Console.WriteLine($"updated {filePath}");
        }
        else
        {
            var updated = existing.TrimEnd() + "\n\n" + wrappedBlock + "\n";
            File.WriteAllText(filePath, updated);
            Console.WriteLine($"updated {filePath}");
        }
    }

    private static int Uninstall(string basePath, string targetAgent, bool isGlobal)
    {
        if (!ValidAgents.Contains(targetAgent))
        {
            Console.Error.WriteLine($"Unknown agent target '{targetAgent}'. Supported targets: claude, cursor, windsurf, cline, antigravity, copilot, kilocode, mimo, codex, gemini, opencode, all.");
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
            DeleteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"));
        }
        else
        {
            RemoveBlock(Path.Combine(basePath, "AGENTS.md"));
            DeleteFile(Path.Combine(basePath, ".opencode", "rules", "csmesh.md"));
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
        const string startTag = "<!-- csmesh-instructions -->";
        const string endTag = "<!-- /csmesh-instructions -->";

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
