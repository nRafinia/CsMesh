using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Telemetry;

namespace CsMesh;

/// <summary>
/// CLI command dispatcher and execution pipeline.
/// </summary>
public static class CliRunner
{
    public static int Run(string[] args)
    {
        if (args is [])
        {
            HelpCommand.Show();
            return Exit.Usage;
        }

        if (args is ["-h" or "--help" or "help", .. var helpTarget])
        {
            return HelpCommand.Show(helpTarget.FirstOrDefault());
        }

        if (args is ["-v" or "--version" or "version"])
        {
            return Emit($"CsMesh {AppVersion.Get()}");
        }

        var cmd = args[0];
        var rest = args[1..];
        var opt = new Options(rest);

        if (opt.Flag("help") || opt.Flag("h") || opt.Positional.Contains("help"))
        {
            return HelpCommand.Show(cmd);
        }

        Dbg.On = opt.Flag("debug")
                 || Environment.GetEnvironmentVariable("CSMESH_DEBUG") == "1"
                 || Environment.GetEnvironmentVariable("CSGRAPH_DEBUG") == "1";
        Telemetry.Telemetry.Disabled = opt.Flag("no-telemetry")
                                       || Environment.GetEnvironmentVariable("CSMESH_NO_TELEMETRY") == "1"
                                       || Environment.GetEnvironmentVariable("CSGRAPH_NO_TELEMETRY") == "1"
                                        || cmd is "usage" or "doctor" or "skill" or "install" or "uninstall" or "version" or "help" or "serve";

        var root = RepositoryLocator.FindRoot(opt.Value("repo") ?? Directory.GetCurrentDirectory());
        Telemetry.Telemetry.Current.Root = root;
        Telemetry.Telemetry.Current.Budget = opt.Int("budget", 600);
        Telemetry.Telemetry.Begin(cmd, rest);

        Dbg.Log($"root={root} caller={Telemetry.Telemetry.Current.Caller} via={Telemetry.Telemetry.Current.CallerVia} " +
                $"tty={Telemetry.Telemetry.Current.Tty} parents={Telemetry.Telemetry.Current.Parents ?? "-"}");

        return cmd switch
        {
            "index" => IndexCommand.Execute(root, opt),
            // 'find' is what people type when they do not know the tool has a name for this.
            "where" or "find" => QueryCommand.Execute(root, opt, "where"),
            "trace" => QueryCommand.Execute(root, opt, "trace"),
            "impl" => QueryCommand.Execute(root, opt, "impl"),
            "blast-radius" or "blast" => QueryCommand.Execute(root, opt, "blast"),
            "entrypoints" => QueryCommand.Execute(root, opt, "entrypoints"),
            "context" => QueryCommand.Execute(root, opt, "context"),
            "path" or "why" => QueryCommand.Execute(root, opt, "path"),
            "cycles" => QueryCommand.Execute(root, opt, "cycles"),
            "unresolved" => QueryCommand.Execute(root, opt, "unresolved"),
            "diff" => QueryCommand.Execute(root, opt, "diff"),
            "changes" => QueryCommand.Execute(root, opt, "changes"),
            "review" => ReviewCommand.Execute(root, opt),
            "silence" or "why-not" => QueryCommand.Execute(root, opt, "silence"),
            "map" => QueryCommand.Execute(root, opt, "map"),
            "serve" => Mcp.McpServer.Run(root),
            "usage" => UsageCommand.Execute(root, opt),
            "doctor" => DoctorCommand.Execute(root, opt),
            "skill" => SkillCommand.Execute(root, opt, SkillMode.Skill),
            "install" => SkillCommand.Execute(root, opt, SkillMode.Install),
            "uninstall" => SkillCommand.Execute(root, opt, SkillMode.Uninstall),
            "version" => Emit($"CsMesh {AppVersion.Get()}"),
            "help" => HelpCommand.Show(rest.FirstOrDefault()),
            _ => Emit($"unknown command '{cmd}'. Try: CsMesh --help", Exit.Usage)
        };
    }

    private static int Emit(string s, int code = Exit.Ok)
    {
        Console.WriteLine(s);
        return code;
    }

    /// <summary>
    /// Dispatches a command and turns an unhandled fault into <see cref="Exit.Internal"/>.
    ///
    /// The catch-all used to report <see cref="Exit.Usage"/> for everything, so a crash inside a
    /// command was indistinguishable from a mistyped flag: an agent branching on the exit code --
    /// the entire contract -- read 64, concluded its own arguments were wrong and re-sent them
    /// instead of reporting a fault. Usage errors are returned rather than thrown everywhere
    /// above this point (no arguments, an unknown command, mutually exclusive flags), so an
    /// exception reaching the catch is an internal fault by definition and 70 is the honest
    /// answer. If a usage error ever starts being thrown, it will surface here as a crash -- loud
    /// and misclassified rather than quiet and plausible.
    /// </summary>
    public static int RunGuarded(string[] args, Func<string[], int> run)
    {
        try
        {
            return run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"csmesh: {ex.Message}");
            if (Dbg.On) Console.Error.WriteLine(ex.StackTrace);
            return Exit.Internal;
        }
    }
}