using CsMesh;
using CsMesh.Common;
using CsMesh.Telemetry;

// First statement on purpose: with CSMESH_TIMINGS=1 this is what separates the part of a slow
// launch that happened before any csmesh code ran from the part the indexer spent.
Timings.Begin();

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
    using (Timings.Phase("telemetry"))
    {
        Telemetry.End(exit);
    }

    Timings.Flush("git-exclude");
    Timings.End();
}

return exit;
