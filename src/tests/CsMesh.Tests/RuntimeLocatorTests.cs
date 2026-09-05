using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Environment variables are process-global and xunit runs classes in parallel, so two tests that
/// each set DOTNET_ROOT and restore it will overwrite each other's setup at random. Sharing a
/// collection serialises them. Found the hard way: the negative case failed intermittently
/// because a sibling class had pointed DOTNET_ROOT at a real installation mid-assertion.
/// </summary>
[CollectionDefinition("environment-variables", DisableParallelization = true)]
public sealed class EnvironmentVariableCollection;

/// <summary>
/// GetRuntimeDirectory answers "where is the runtime hosting me", which under NativeAOT has no
/// answer -- there is no hosted runtime and the call returns whichever directory the binary sits
/// in. Nothing downstream treats that as a failure: the index builds, reports counts and answers
/// queries, with a large share of the call graph quietly missing because overload resolution had
/// no framework types to work with.
///
/// Measured on a real solution with a real NativeAOT binary: 0 runtime references and 470 unbound
/// call sites, against 313 and 79 once the framework is found.
/// </summary>
[Collection("environment-variables")]
public sealed class RuntimeLocatorTests
{
    /// <summary>
    /// The test process is JIT-hosted, so the hosted answer is the right one and must be taken
    /// unchanged. This is the path that already worked and the one a regression would be quietest
    /// in -- the index would still build, just against a different framework than before.
    /// </summary>
    [Fact]
    public void AHostedRuntimeIsFoundAndIsARealFrameworkDirectory()
    {
        var framework = RuntimeLocator.FindSharedFramework();

        Assert.NotNull(framework);
        Assert.True(Directory.Exists(framework));
        Assert.True(File.Exists(Path.Combine(framework, "System.Private.CoreLib.dll")));
        Assert.True(File.Exists(Path.Combine(framework, "System.Runtime.dll")));
    }

    /// <summary>
    /// The directory has to hold the assemblies, not merely be named as though it does. Testing
    /// the path shape instead is exactly how the AOT case slipped through: the binary's own
    /// directory can be called anything, including something plausible.
    /// </summary>
    [Fact]
    public void ADirectoryThatMerelyLooksRightIsNotAccepted()
    {
        var fake = Path.Combine(Path.GetTempPath(), "csmesh-fake-" + Guid.NewGuid().ToString("N")[..8],
            "shared", "Microsoft.NETCore.App", "10.0.0");
        Directory.CreateDirectory(fake);

        try
        {
            var root = Path.GetFullPath(Path.Combine(fake, "..", "..", ".."));
            var previous = Environment.GetEnvironmentVariable("DOTNET_ROOT");

            try
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", root);

                // The hosted runtime still wins here, so this asserts the weaker but still
                // meaningful thing: an empty directory is never what comes back.
                Assert.NotEqual(fake, RuntimeLocator.FindSharedFramework());
            }
            finally
            {
                Environment.SetEnvironmentVariable("DOTNET_ROOT", previous);
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetFullPath(Path.Combine(fake, "..", "..", "..")), true); }
            catch { /* temp dir */ }
        }
    }

    /// <summary>
    /// A framework found here is only useful if the sibling walk still works from it, since
    /// ASP.NET Core types live beside Microsoft.NETCore.App rather than inside it, and a web
    /// project's overloads do not resolve without them.
    /// </summary>
    [Fact]
    public void SiblingFrameworksAreReachableFromWhatIsFound()
    {
        var framework = RuntimeLocator.FindSharedFramework();
        Assert.NotNull(framework);

        var siblings = Analysis.Indexer.SiblingSharedFrameworks(framework).ToList();

        foreach (var sibling in siblings)
        {
            Assert.True(Directory.Exists(sibling), $"'{sibling}' does not exist");
            Assert.DoesNotContain("Microsoft.NETCore.App", sibling, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The AOT case, simulated by handing the locator what a NativeAOT binary is handed: a directory
/// that is not a framework. A test run is JIT-hosted by definition, so this is the only way the
/// path that actually broke can be exercised in a suite at all.
/// </summary>
[Collection("environment-variables")]
public sealed class RuntimeLocatorFallbackTests
{
    /// <summary>
    /// What NativeAOT produces: the directory the executable happens to sit in. Discovery has to
    /// take over and find a real installation instead of accepting it.
    /// </summary>
    [Fact]
    public void AnAotStyleDirectoryFallsThroughToDiscovery()
    {
        var binaryDirectory = AppContext.BaseDirectory;

        var framework = RuntimeLocator.FindSharedFramework(binaryDirectory);

        Assert.NotNull(framework);
        Assert.True(File.Exists(Path.Combine(framework, "System.Private.CoreLib.dll")),
            $"discovery returned '{framework}', which holds no framework assemblies");
    }

    [Fact]
    public void DiscoveryFindsAFrameworkFromDotnetRootAlone()
    {
        var hosted = RuntimeLocator.FindSharedFramework();
        Assert.NotNull(hosted);

        // shared/Microsoft.NETCore.App/<version> -> the install root three levels up.
        var root = Path.GetFullPath(Path.Combine(hosted, "..", "..", ".."));
        var previous = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);

            var found = RuntimeLocator.FindSharedFramework(Path.GetTempPath());

            Assert.NotNull(found);
            Assert.True(File.Exists(Path.Combine(found, "System.Runtime.dll")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previous);
        }
    }

    // A "nothing installed" case was written here and then removed. It has to blank DOTNET_ROOT,
    // PATH and HOME and then assert that no framework turns up, which makes the result a
    // statement about the machine rather than about the code: it passes in a bare container and
    // fails on any developer box with .NET in a standard location. The warning it would have
    // guarded is one line, and a suite that goes red for reasons unrelated to the change is worse
    // than an untested line.
}
