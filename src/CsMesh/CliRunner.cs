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
                                       || cmd is "usage" or "doctor" or "skill" or "version" or "help";

        var root = RepositoryLocator.FindRoot(opt.Value("repo") ?? Directory.GetCurrentDirectory());
        Telemetry.Telemetry.Current.Root = root;
        Telemetry.Telemetry.Current.Budget = opt.Int("budget", 600);
        Telemetry.Telemetry.Begin(cmd, rest);

        Dbg.Log($"root={root} caller={Telemetry.Telemetry.Current.Caller} via={Telemetry.Telemetry.Current.CallerVia} " +
                $"tty={Telemetry.Telemetry.Current.Tty} parents={Telemetry.Telemetry.Current.Parents ?? "-"}");

        return cmd switch
        {
            "index" => IndexCommand.Execute(root, opt),
            "trace" => QueryCommand.Execute(root, opt, "trace"),
            "impl" => QueryCommand.Execute(root, opt, "impl"),
            "blast-radius" or "blast" => QueryCommand.Execute(root, opt, "blast"),
            "entrypoints" => QueryCommand.Execute(root, opt, "entrypoints"),
            "usage" => UsageCommand.Execute(root, opt),
            "doctor" => DoctorCommand.Execute(root, opt),
            "skill" => SkillCommand.Execute(root, opt),
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
}
