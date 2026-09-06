using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Console.Out is one property on one process. A test that redirects it to measure what a command
/// printed is also redirecting it for every test running beside it, so any output those produce
/// lands in the wrong buffer.
///
/// The symptom is not a plausible one. The captured text gains a line from somewhere else, and the
/// assertion that fails is whichever one happened to parse it -- 'is this exactly one JSON frame'
/// reports two, and the MCP exchange tries to parse an index progress line as a JSON-RPC message.
/// Neither points at the real cause, and both depend on timing, so the suite was green on Linux and
/// red on Windows from the same commit.
///
/// Sharing a collection is what stops it: xunit does not run classes in the same collection
/// concurrently.
/// </summary>
[CollectionDefinition("console-capture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;
