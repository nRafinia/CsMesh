using CsMesh;
using CsMesh.Common;
using CsMesh.Telemetry;

var exit = Exit.Usage;
try
{
    exit = CliRunner.Run(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"csmesh: {ex.Message}");
    if (Dbg.On) Console.Error.WriteLine(ex.StackTrace);
    exit = Exit.Usage;
}
finally
{
    Telemetry.End(exit);
}

return exit;