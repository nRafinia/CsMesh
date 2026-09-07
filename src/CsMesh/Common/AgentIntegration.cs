using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsMesh.Common;

/// <summary>
/// Registers csmesh as an MCP server, and drops in the grep hook, next to wherever the skill files
/// went.
///
/// Kept apart from SkillCommand because the failure modes are different in kind. A skill file is
/// ours and can be overwritten; these two are the user's own configuration, shared with every
/// other tool they have installed, and clobbering an mcpServers block would take their unrelated
/// servers with it.
/// </summary>
public static class AgentIntegration
{
    /// <summary>
    /// Where this binary actually is.
    ///
    /// Writing "command": "csmesh" only works when the binary is on PATH, and the machines where
    /// it is not are exactly the ones where a silent failure is hardest to diagnose: the client
    /// reports a server that would not start and says nothing about why.
    /// </summary>
    public static string BinaryPath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path)) return path;

        // Under a hosted runtime ProcessPath is the dotnet host, so fall back to the name and
        // accept that PATH has to resolve it.
        return OperatingSystem.IsWindows() ? "csmesh.exe" : "csmesh";
    }

    /// <summary>
    /// Adds or updates the csmesh entry in an MCP config file, leaving every other server alone.
    ///
    /// Reads what is there, edits one key, writes it back. A whole-file write would be simpler and
    /// would silently delete the user's other servers the first time they had any.
    /// </summary>
    /// <summary>
    /// Config files that hold an mcpServers block, for the whole machine.
    ///
    /// Every one of these uses the same shape -- mcpServers, then command and args -- which is
    /// what makes a single writer enough. Verified against a real install rather than assumed,
    /// because guessing a schema is what made the first version of the grep hook do nothing.
    /// </summary>
    public static IEnumerable<string> GlobalServerTargets(string home)
    {
        yield return Path.Combine(home, ".claude.json");
        yield return Path.Combine(home, ".claude", ".mcp.json");
        yield return Path.Combine(home, ".cursor", "mcp.json");
        yield return Path.Combine(home, ".ai", "mcp", "mcp.json");
        yield return Path.Combine(home, ".gemini", "settings.json");
        yield return Path.Combine(home, ".gemini", "config", "mcp_config.json");

        // Antigravity moved its config more than once and the surfaces do not agree yet: the IDE
        // has used .gemini/antigravity, the CLI .gemini/antigravity-cli, and the shared path is
        // .gemini/config. Writing all three is cheap and writing the wrong one is invisible --
        // the server simply never appears, with nothing anywhere saying why.
        yield return Path.Combine(home, ".gemini", "antigravity", "mcp_config.json");
        yield return Path.Combine(home, ".gemini", "antigravity-cli", "mcp_config.json");

        if (OperatingSystem.IsWindows())
        {
            var roaming = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(roaming))
                yield return Path.Combine(roaming, "Claude", "claude_desktop_config.json");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "Claude",
                "claude_desktop_config.json");
        }
        else
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                         ?? Path.Combine(home, ".config");
            yield return Path.Combine(config, "Claude", "claude_desktop_config.json");
        }
    }

    /// <summary>Config files a repository carries with it.</summary>
    public static IEnumerable<string> ProjectServerTargets(string repoRoot)
    {
        yield return Path.Combine(repoRoot, ".mcp.json");
        yield return Path.Combine(repoRoot, ".cursor", "mcp.json");

        // Antigravity's workspace-scoped location. Its global config is a separate file, so a
        // project install that skipped this left Antigravity with nothing at all.
        yield return Path.Combine(repoRoot, ".agents", "mcp_config.json");
    }

    public static bool RegisterServer(string configPath, string? repoRoot, out string outcome)
    {
        try
        {
            JsonObject root;

            if (File.Exists(configPath))
            {
                var existing = File.ReadAllText(configPath);
                root = string.IsNullOrWhiteSpace(existing)
                    ? new JsonObject()
                    : JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            var replaced = servers["csmesh"] != null;

            // A machine-wide registration must not name a repository. Pinning one would answer
            // every other project's questions from the wrong graph -- confidently, and with no
            // sign anything was wrong. Left off, serve resolves the repository from the directory
            // the client launches it in, which is the project directory in every client checked.
            var args = repoRoot == null
                ? new JsonArray("serve")
                : new JsonArray("serve", "--repo", Path.GetFullPath(repoRoot));

            servers["csmesh"] = new JsonObject
            {
                ["command"] = BinaryPath(),
                ["args"] = args
            };

            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = replaced ? "updated" : "added";
            return true;
        }
        catch (Exception ex)
        {
            // A malformed config the user hand-edited is the likely case, and rewriting it from
            // scratch would lose whatever they were in the middle of.
            outcome = ex.Message;
            return false;
        }
    }

    /// <summary>Removes the csmesh entry without disturbing anything else in the file.</summary>
    public static bool UnregisterServer(string configPath, out string outcome)
    {
        outcome = "absent";

        try
        {
            if (!File.Exists(configPath)) return false;

            if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root ||
                root["mcpServers"] is not JsonObject servers ||
                servers["csmesh"] == null)
            {
                return false;
            }

            servers.Remove("csmesh");

            // An empty mcpServers is noise, but only remove it if we are the reason it is empty.
            if (servers.Count == 0) root.Remove("mcpServers");

            File.WriteAllText(configPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = "removed";
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The grep hook: an executable script, and a PreToolUse entry pointing at it.
    ///
    /// Advisory by design. It never blocks, because grep over .cs is correct for string literals,
    /// log messages and TODOs, and a hook that refused those would be wrong more often than right.
    /// </summary>
    public static bool InstallHook(string claudeDir, out string outcome)
    {
        try
        {
            var scriptName = OperatingSystem.IsWindows() ? "csmesh-grep-hint.ps1" : "csmesh-grep-hint.sh";
            var hooksDir = Path.Combine(claudeDir, "hooks");
            Directory.CreateDirectory(hooksDir);

            var scriptPath = Path.Combine(hooksDir, scriptName);
            File.WriteAllText(scriptPath, OperatingSystem.IsWindows() ? HookText.PowerShell : HookText.Bash);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            var settingsPath = Path.Combine(claudeDir, "settings.json");
            JsonObject root = File.Exists(settingsPath) && JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject parsed
                ? parsed
                : new JsonObject();

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            if (hooks["PreToolUse"] is not JsonArray preToolUse)
            {
                preToolUse = new JsonArray();
                hooks["PreToolUse"] = preToolUse;
            }

            var command = OperatingSystem.IsWindows()
                ? $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\""
                : scriptPath;

            // Matched on the command text, so re-running install updates the entry rather than
            // stacking a second copy that fires alongside the first.
            var mine = preToolUse.FirstOrDefault(entry =>
                entry?["hooks"] is JsonArray inner &&
                inner.Any(h => h?["command"]?.GetValue<string>()?.Contains("csmesh-grep-hint", StringComparison.Ordinal) == true));

            if (mine != null) preToolUse.Remove(mine);

            preToolUse.Add(new JsonObject
            {
                ["matcher"] = "Grep",
                ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command
                })
            });

            File.WriteAllText(settingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = scriptPath;
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// The same nudge for Antigravity, whose hook format is not Claude's.
    ///
    /// The event is BeforeTool rather than PreToolUse, the matcher names the tool differently, and
    /// the hooks array holds strings rather than objects. Close enough to look interchangeable and
    /// not close enough to be -- writing Claude's shape here produces a file the IDE accepts and
    /// ignores.
    /// </summary>
    public static bool InstallGeminiHook(string settingsPath, out string outcome)
    {
        try
        {
            JsonObject root = File.Exists(settingsPath) &&
                              JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject parsed
                ? parsed
                : new JsonObject();

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            if (hooks["BeforeTool"] is not JsonArray beforeTool)
            {
                beforeTool = new JsonArray();
                hooks["BeforeTool"] = beforeTool;
            }

            var mine = beforeTool.FirstOrDefault(e =>
                e?["matcher"]?.GetValue<string>()?.Contains("grep", StringComparison.OrdinalIgnoreCase) == true &&
                e.ToJsonString().Contains("csmesh", StringComparison.Ordinal));

            if (mine != null) beforeTool.Remove(mine);

            // Written to stderr, which is where an advisory belongs: it reaches the model as
            // context without being mistaken for the tool's own output.
            const string message =
                "Reminder: prefer csmesh (where, trace, impl, blast-radius) over grep for C# code " +
                "discovery; it resolves DI bindings and mediator dispatch that grep cannot. " +
                "Run 'csmesh index' first if the repository is not indexed.";

            beforeTool.Add(new JsonObject
            {
                ["matcher"] = "grep_search|file_search",
                ["hooks"] = new JsonArray($"@{{type=command; command=echo '{message}' >&2}}")
            });

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(settingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = "added";
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    public static bool UninstallGeminiHook(string settingsPath, out string outcome)
    {
        outcome = "absent";

        try
        {
            if (!File.Exists(settingsPath) ||
                JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root ||
                root["hooks"] is not JsonObject hooks ||
                hooks["BeforeTool"] is not JsonArray beforeTool)
            {
                return false;
            }

            var mine = beforeTool
                .Where(e => e?.ToJsonString().Contains("csmesh", StringComparison.Ordinal) == true)
                .ToList();

            if (mine.Count == 0) return false;

            foreach (var entry in mine) beforeTool.Remove(entry);

            if (beforeTool.Count == 0) hooks.Remove("BeforeTool");
            if (hooks.Count == 0) root.Remove("hooks");

            File.WriteAllText(settingsPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = "removed";
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    public static bool InstallAntigravityHook(string hooksPath, out string outcome)
    {
        try
        {
            JsonObject root = File.Exists(hooksPath) &&
                              JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject parsed
                ? parsed
                : new JsonObject();

            const string hookName = "csmesh-grep-hint";
            var command = OperatingSystem.IsWindows()
                ? "cmd /c \"echo ADVISORY: prefer csmesh (where, trace, impl, blast-radius) over grep for C# code discovery. 1>&2\""
                : "echo 'ADVISORY: prefer csmesh (where, trace, impl, blast-radius) over grep for C# code discovery.' >&2";

            var hookDef = new JsonObject
            {
                ["PreToolUse"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["matcher"] = "grep_search",
                        ["hooks"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "command",
                                ["command"] = command
                            }
                        }
                    }
                }
            };

            root[hookName] = hookDef;

            var directory = Path.GetDirectoryName(hooksPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(hooksPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = "added";
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    public static bool UninstallAntigravityHook(string hooksPath, out string outcome)
    {
        outcome = "absent";

        try
        {
            if (!File.Exists(hooksPath) ||
                JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root ||
                root["csmesh-grep-hint"] == null)
            {
                return false;
            }

            root.Remove("csmesh-grep-hint");

            File.WriteAllText(hooksPath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            outcome = "removed";
            return true;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }

    /// <summary>Takes the hook entry back out and deletes the script.</summary>
    public static bool UninstallHook(string claudeDir, out string outcome)
    {
        outcome = "absent";
        var removed = false;

        try
        {
            foreach (var name in new[] { "csmesh-grep-hint.sh", "csmesh-grep-hint.ps1" })
            {
                var script = Path.Combine(claudeDir, "hooks", name);
                if (File.Exists(script)) { File.Delete(script); removed = true; }
            }

            var settingsPath = Path.Combine(claudeDir, "settings.json");

            if (File.Exists(settingsPath) &&
                JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject root &&
                root["hooks"] is JsonObject hooks &&
                hooks["PreToolUse"] is JsonArray preToolUse)
            {
                var mine = preToolUse.Where(entry =>
                        entry?["hooks"] is JsonArray inner &&
                        inner.Any(h => h?["command"]?.GetValue<string>()?.Contains("csmesh-grep-hint", StringComparison.Ordinal) == true))
                    .ToList();

                foreach (var entry in mine) { preToolUse.Remove(entry); removed = true; }

                if (preToolUse.Count == 0) hooks.Remove("PreToolUse");
                if (hooks.Count == 0) root.Remove("hooks");

                File.WriteAllText(settingsPath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }

            if (removed) outcome = "removed";
            return removed;
        }
        catch (Exception ex)
        {
            outcome = ex.Message;
            return false;
        }
    }
}
