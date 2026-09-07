using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// .razor files are invisible to the indexer -- their generated types normally live only in the
/// source generator's memory, never on disk. Before anything can be done about that, the graph
/// has to know they exist at all, so 'doctor' can tell a missing-namespace diagnostic apart from a
/// genuinely broken reference.
/// </summary>
public sealed class RazorFileCountTests : IDisposable
{
    private readonly string _root;

    public RazorFileCountTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-razorcount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src", "Pages"));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void RazorAndCshtmlFilesAreCountedOnTheGraph()
    {
        Write("src/Pages/Counter.razor", "<h1>@count</h1>\n@code { int count; }");
        Write("src/Pages/Index.razor", "<h1>Home</h1>");
        Write("src/Views/Legacy.cshtml", "<h1>Legacy</h1>");
        Write("src/Program.cs", "System.Console.WriteLine(\"hi\");");

        var graph = Indexer.Build(_root);

        Assert.Equal(3, graph.RazorFileCount);
    }

    /// <summary>
    /// A repository with no Razor at all must report zero, not an absent or default-looking value
    /// that could be mistaken for "not measured".
    /// </summary>
    [Fact]
    public void ARepositoryWithNoRazorFilesReportsZero()
    {
        Write("src/Program.cs", "namespace Demo; public class A { }");

        var graph = Indexer.Build(_root);

        Assert.Equal(0, graph.RazorFileCount);
    }

    /// <summary>Files under obj/, bin/ etc. are build output, not source -- must not be counted twice.</summary>
    [Fact]
    public void RazorFilesUnderSkippedDirectoriesAreNotCounted()
    {
        Write("src/Pages/Counter.razor", "<h1>@count</h1>");
        Write("src/obj/Debug/net10.0/generated/Counter_razor.g.razor", "// not real source");
        Write("src/bin/Debug/net10.0/publish/Copy.razor", "// build output");

        var graph = Indexer.Build(_root);

        Assert.Equal(1, graph.RazorFileCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}

/// <summary>
/// 'doctor' is where an agent actually learns that a CS0246 about a missing namespace is expected
/// once Razor is involved, rather than a broken reference to go hunting for.
/// </summary>
[Collection("console-capture")]
public sealed class RazorDoctorMessageTests : IDisposable
{
    private readonly string _root;

    public RazorDoctorMessageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-razordoctor-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "src"));
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string Capture(Func<int> run)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            run();
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }

    [Fact]
    public void DoctorExplainsTheMissingRazorTypesInsteadOfLeavingCS0246Unexplained()
    {
        Write("src/Pages/Counter.razor", "<h1>@count</h1>\n@code { int count; }");
        Write("src/Program.cs", "namespace Demo; public class A { }");

        IndexCommand.Execute(_root, new Options([]));

        var raw = Capture(() => DoctorCommand.Execute(_root, new Options([])));

        Assert.Contains("razor", raw, StringComparison.Ordinal);
        Assert.Contains("1 .razor/.cshtml file(s) found", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorSaysNothingAboutRazorWhenThereIsNone()
    {
        Write("src/Program.cs", "namespace Demo; public class A { }");

        IndexCommand.Execute(_root, new Options([]));

        var raw = Capture(() => DoctorCommand.Execute(_root, new Options([])));

        Assert.DoesNotContain(".razor/.cshtml file(s) found", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonReportCarriesTheCountToo()
    {
        Write("src/Pages/Counter.razor", "<h1>@count</h1>");
        Write("src/Pages/Other.razor", "<h1>other</h1>");
        Write("src/Program.cs", "namespace Demo; public class A { }");

        IndexCommand.Execute(_root, new Options([]));

        var raw = Capture(() => DoctorCommand.Execute(_root, new Options(["--json"])));
        var report = System.Text.Json.JsonSerializer.Deserialize(
            raw.Trim(), AppJsonContext.Default.DoctorReport)!;

        Assert.Equal(2, report.RazorFileCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }
}
