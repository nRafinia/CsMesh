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
/// index with it (exit 70). This denies the list permission on a real directory and checks the walk
/// both skips it and names it in the debug log. Captures Console.Error, so the class joins the
/// console-capture collection.
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
    [SupportedOSPlatform("windows")]
    public void An_inaccessible_directory_is_skipped_and_named_in_the_debug_log()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Fail("list-permission denial needs the Windows access-control API");
        }

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Kept.cs"),
            "namespace Demo; public sealed class Kept { }");

        var denied = Path.Combine(_root, "denied");
        Directory.CreateDirectory(denied);
        File.WriteAllText(Path.Combine(denied, "Hidden.cs"),
            "namespace Demo; public sealed class Hidden { }");

        var identity = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(identity, FileSystemRights.ListDirectory, AccessControlType.Deny);
        var info = new DirectoryInfo(denied);
        var security = info.GetAccessControl();
        security.AddAccessRule(rule);

        var originalError = Console.Error;
        var captured = new StringWriter();
        var debugWasOn = Dbg.On;
        try
        {
            info.SetAccessControl(security);
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
            security.RemoveAccessRule(rule);
            info.SetAccessControl(security);
        }
    }
}
