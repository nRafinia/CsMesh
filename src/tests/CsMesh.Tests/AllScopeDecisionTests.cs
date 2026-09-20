using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// '--all' overrides the solution and indexes every project. doctor's scope line read the decision
/// string ProjectScope.Everything had always carried -- "no project files; indexing every source
/// file" -- which is false exactly when --all was asked for on a repository that does have projects.
/// The line has to say what was requested.
/// </summary>
[Collection("console-capture")]
public sealed class AllScopeDecisionTests : IDisposable
{
    private readonly string _root;

    public AllScopeDecisionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-allscope-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
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
    public void Doctor_names_all_when_projects_are_indexed_under_all()
    {
        Write("A/A.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>" +
            "<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        Write("A/Program.cs", "System.Console.WriteLine(1);");
        Write("B/B.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>" +
            "<ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        Write("B/Lib.cs", "namespace B { public class L { } }");
        Write("App.slnx", "<Solution><Project Path=\"A/A.csproj\" /></Solution>");

        var graph = Indexer.Build(_root, includeAllProjects: true);

        Assert.Contains("--all", graph.ScopeDecision, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no project files", graph.ScopeDecision, StringComparison.OrdinalIgnoreCase);

        GraphStore.Save(graph);
        var output = Capture(() => DoctorCommand.Execute(_root, new Options([])));

        Assert.Contains("--all", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no project files", output, StringComparison.OrdinalIgnoreCase);
    }
}
