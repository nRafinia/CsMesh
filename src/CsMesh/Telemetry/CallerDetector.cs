namespace CsMesh.Telemetry;

/// <summary>
/// Detects execution environments and caller processes via environment variables and process trees.
/// </summary>
public static class CallerDetector
{
    private static readonly (string Env, string Name)[] EnvMarkers =
    {
        ("CLAUDECODE",             "claude-code"),
        ("CLAUDE_CODE_ENTRYPOINT", "claude-code"),
        ("CLAUDE_CODE_SSE_PORT",   "claude-code"),
        ("CLAUDE_PROJECT_DIR",     "claude-code"),
        ("CODEX_SANDBOX",          "codex"),
        ("OPENAI_CODEX_HOME",      "codex"),
        ("CURSOR_TRACE_ID",        "cursor"),
        ("CLINE_TASK_ID",          "cline"),
        ("CLINE_WORKSPACE",        "cline"),
        ("ROO_CODE_TASK",          "roo-code"),
        ("KILOCODE_TASK",          "kilo-code"),
        ("AIDER_MODEL",            "aider"),
        ("GEMINI_CLI",             "gemini-cli"),
        ("WINDSURF_SESSION",       "windsurf"),
        ("CONTINUE_SESSION_ID",    "continue"),
        ("OPENCODE_SESSION",       "opencode"),
        ("MIMO_SESSION",           "mimo"),
        ("AGENT_NAME",             "generic-agent"),
    };

    private static readonly string[] AgentProcessNames =
    {
        "claude", "codex", "cline", "cursor", "aider", "opencode", "goose", "gemini", "windsurf", "mimo"
    };

    public static (string Caller, string Via) Detect()
    {
        var forced = Environment.GetEnvironmentVariable("CSMESH_CALLER")
            ?? Environment.GetEnvironmentVariable("CSGRAPH_CALLER");
        if (!string.IsNullOrWhiteSpace(forced))
        {
            return (forced, "env:CSMESH_CALLER");
        }

        foreach (var (env, name) in EnvMarkers)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(env)))
            {
                return (name, "env:" + env);
            }
        }

        var chain = ParentChain();
        if (chain != null)
        {
            foreach (var proc in chain.Split('<'))
            {
                foreach (var known in AgentProcessNames)
                {
                    if (proc.Contains(known, StringComparison.OrdinalIgnoreCase))
                    {
                        return (known, "proc:" + proc.Trim());
                    }
                }
            }
        }

        if (Environment.GetEnvironmentVariable("TERM_PROGRAM") == "vscode")
        {
            return ("vscode-ext", "env:TERM_PROGRAM");
        }

        return Console.IsOutputRedirected
            ? ("unknown-automation", "tty:redirected")
            : ("human", "tty:interactive");
    }

    public static string? SessionHint() =>
        Environment.GetEnvironmentVariable("CLAUDE_SESSION_ID")
        ?? Environment.GetEnvironmentVariable("CLINE_TASK_ID")
        ?? Environment.GetEnvironmentVariable("CURSOR_TRACE_ID")
        ?? Environment.GetEnvironmentVariable("CSMESH_SESSION")
        ?? Environment.GetEnvironmentVariable("CSGRAPH_SESSION");

    /// <summary>
    /// Reads parent process chain from /proc on Linux systems.
    /// </summary>
    public static string? ParentChain()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return null;

            var parts = new List<string>();
            var pid = Environment.ProcessId;

            for (var depth = 0; depth < 6 && pid > 1; depth++)
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                var open = stat.IndexOf('(');
                var close = stat.LastIndexOf(')');
                if (open < 0 || close < 0) break;

                var comm = stat[(open + 1)..close];
                if (depth > 0) parts.Add(comm);

                var rest = stat[(close + 2)..].Split(' ');
                if (rest.Length < 2 || !int.TryParse(rest[1], out pid)) break;
            }

            return parts.Count == 0 ? null : string.Join(" < ", parts);
        }
        catch
        {
            return null;
        }
    }
}
