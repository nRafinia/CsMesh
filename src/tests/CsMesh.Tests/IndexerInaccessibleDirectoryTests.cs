using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using CsMesh.Analysis;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// Source enumeration must survive a directory the process may not read.
///
/// The old call, Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories), maps to an
/// EnumerationOptions whose IgnoreInaccessible is false, so one refused directory -- a legacy
/// profile junction, a permission-stripped cache -- aborted the whole enumeration and took the
/// index with it (exit 70). This denies the list permission on a real directory -- an ACL rule on
/// Windows, mode 000 elsewhere -- and checks the walk both skips it and names it in the debug log.
/// The denial is asserted to have taken before the result is trusted, because root bypasses Unix
/// file modes and an owner can hold a right the deny was meant to remove. Captures Console.Error,
/// so the class joins the console-capture collection.
/// </summary>
[Collection("console-capture")]
public sealed class IndexerInaccessibleDirectoryTests : IDisposable
{
    private readonly string _root;

    public IndexerInaccessibleDirectoryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-inaccessible-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void An_inaccessible_directory_is_skipped_and_named_in_the_debug_log()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Kept.cs"),
            "namespace Demo; public sealed class Kept { }");

        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "Hidden.cs"),
            "namespace Demo; public sealed class Hidden { }");

        var restore = DenyListing(denied);

        var originalError = Console.Error;
        var captured = new StringWriter();
        var debugWasOn = Dbg.On;
        try
        {
            // Precondition: the denial has to be real before the result means anything. Running as
            // root -- the one account Unix file modes do not restrain -- leaves the directory
            // readable, and without this check the test would pass by finding nothing to skip.
            var refused = false;
            try
            {
                _ = Directory.GetFiles(denied, "*", new EnumerationOptions { IgnoreInaccessible = false });
            }
            catch (UnauthorizedAccessException)
            {
                refused = true;
            }

            if (!refused)
            {
                Assert.Fail(
                    $"listing '{denied}' was expected to be refused but succeeded; the denial did " +
                    "not take (running as root, or a platform that ignored it), so this test could " +
                    "not tell a skipped directory from an empty one.");
            }

            Dbg.On = true;
            Console.SetError(captured);

            var files = Indexer.EnumerateSourceFiles(_root).Select(Path.GetFileName).ToList();

            Assert.Contains("Kept.cs", files);
            Assert.DoesNotContain("Hidden.cs", files);
            Assert.Contains(denied, captured.ToString());
        }
        finally
        {
            Console.SetError(originalError);
            Dbg.On = debugWasOn;
            restore();
        }
    }

    /// <summary>
    /// Removes the list permission on <paramref name="directory"/> and returns the action that puts
    /// it back. Windows denies through an ACL rule on the current user; elsewhere the Unix mode is
    /// set to none. Both are restored by the caller's finally.
    /// </summary>
    private static Action DenyListing(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return DenyUnix(directory);
        }

        return DenyWindows(directory);
    }

    [UnsupportedOSPlatform("windows")]
    private static Action DenyUnix(string directory)
    {
        var original = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.None);
        return () => File.SetUnixFileMode(directory, original);
    }

    [SupportedOSPlatform("windows")]
    private static Action DenyWindows(string directory)
    {
        var identity = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(identity, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
        return () =>
        {
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        };
    }
}
