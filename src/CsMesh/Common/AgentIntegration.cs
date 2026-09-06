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
    public static bool RegisterServer(string configPath, string repoRoot, out string outcome)
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

            servers["csmesh"] = new JsonObject
            {
                ["command"] = BinaryPath(),
                ["args"] = new JsonArray("serve", "--repo", Path.GetFullPath(repoRoot))
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
