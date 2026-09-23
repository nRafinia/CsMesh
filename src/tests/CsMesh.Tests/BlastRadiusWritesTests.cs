using CsMesh.Analysis;
using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Models;
using CsMesh.Storage;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// blast-radius --writes answers "where is this written": only sites whose member-access edge
/// carries the Write bit, never the readers. A write through an interface-typed reference lands on
/// the interface member, so one reverse interface hop pulls those writers in and marks them, which
/// is the only way a caller writing through the interface shows up for the implementation.
/// </summary>
[Collection("console-capture")]
public sealed class BlastRadiusWritesTests
{
    private const string Source = """
        namespace Demo;
        public interface I { int P { get; set; } }
        public class A : I { public int P { get; set; } }
        public class Callers
        {
            public void DirectWrite(A a) { a.P = 1; }
            public void InterfaceWrite(I i) { i.P = 2; }
            public void Reads(A a) { var x = a.P; }
        }
        """;

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; }

        public Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "csmesh-writes-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "A.cs"), Source);
            GraphStore.Save(Indexer.Build(Root));
        }

        public (int Exit, string Text) Run(params string[] args)
        {
            var original = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                return (QueryCommand.Execute(Root, new Options(args), "blast"), buffer.ToString());
            }
            finally
            {
                Console.SetOut(original);
            }
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public void Only_writers_are_returned_and_interface_writers_are_marked()
    {
        using var sandbox = new Sandbox();

        var (exit, text) = sandbox.Run("A.P", "--writes");

        Assert.Equal(Exit.Ok, exit);
        Assert.Contains("Callers.DirectWrite", text);
        Assert.Contains("Callers.InterfaceWrite", text);
        Assert.Contains("[via-interface]", text);
        Assert.DoesNotContain("Callers.Reads", text);
    }

    [Fact]
    public void Exit_codes_are_found_not_found_and_over_budget()
    {
        using var sandbox = new Sandbox();

        Assert.Equal(Exit.Ok, sandbox.Run("A.P", "--writes").Exit);
        Assert.Equal(Exit.NotFound, sandbox.Run("Demo.NoSuchThing", "--writes").Exit);
        Assert.Equal(Exit.OverBudget, sandbox.Run("A.P", "--writes", "--budget", "1").Exit);
    }
}
