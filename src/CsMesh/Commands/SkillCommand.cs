using System.Collections.Frozen;
using CsMesh.Common;
using CsMesh.Skill;

namespace CsMesh.Commands;

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

    public static int Execute(string root, Options opt)
    {
        if (opt.Flag("help") || opt.Flag("h") || opt.Positional.Contains("help"))
        {
            return HelpCommand.Show("skill");
        }

        if (!opt.Flag("install"))
        {
            Console.WriteLine(SkillText.Markdown);
            return Exit.Ok;
        }

        var isGlobal = opt.Flag("global") || opt.Flag("g");
        var targetAgent = opt.Get("agent", "all").ToLowerInvariant();

        if (isGlobal)
        {
            var home = GetHomeDir();
            Console.WriteLine($"Installing csmesh skill globally to user config: {home}\n");
            return Install(home, targetAgent, isGlobal: true);
        }

        return Install(root, targetAgent, isGlobal: false);
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
}
