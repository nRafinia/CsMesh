using CsMesh;
using CsMesh.Common;
using CsMesh.Telemetry;

// The initial value matters. If anything ever escapes the guard itself, the finally still records
// a crash: telemetry must never log a fault as a misused command line, which is what reporting
// Exit.Usage here did.
var exit = Exit.Internal;
try
{
    exit = CliRunner.RunGuarded(args, CliRunner.Run);
}
finally
{
    Telemetry.End(exit);
}

return exit;
