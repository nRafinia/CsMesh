using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// <c>Telemetry.Current</c> is one <c>Invocation</c> instance on one process, and every command
/// resolves its budget into it (<c>WriteFor</c> assigns <c>Current.Budget</c>). A test that pins the
/// value the writer resolved and a test beside it that resolves a different kind's budget write the
/// same field. On a parallel run the pinning assertion can read the other test's number, which is
/// why <c>An_explicit_budget_overrides_the_kind_default_for_both</c> went red once with 123 expected
/// while a concurrent theory resolved 600 or 850.
///
/// The console-capture collection already has this shape, but for <c>Console.Out</c>. This one is
/// for the telemetry singleton so the two can stay named for what they protect. Disabling
/// parallelization for the collection is what keeps these writers from overlapping any other test.
/// </summary>
[CollectionDefinition("telemetry-state", DisableParallelization = true)]
public sealed class TelemetryStateCollection;
