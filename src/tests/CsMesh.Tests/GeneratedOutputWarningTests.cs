using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// CS8795 is the compiler saying a partial method has no implementation part, which is what a source
/// generator that did not run leaves behind. Its members are then missing from the graph and every
/// call to them is unbound, with nothing in doctor naming the one project-file change that fixes it.
/// The warning is read from the diagnostics the index already captured, so doctor compiles nothing.
/// </summary>
[Collection("console-capture")]
public sealed class GeneratedOutputWarningTests : IDisposable
{
    private readonly string _root;

    public GeneratedOutputWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-genwarn-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private const string Csproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    private const string CsprojNoImplicitUsings =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
        + "<ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable></PropertyGroup></Project>";

    /// <summary>
    /// Nine distinct declaration errors, eight of them occurring twice, and one CS8795. This is a
    /// project whose generator is off while the rest of the tree is also broken, so the one
    /// actionable id is the least frequent -- exactly what a count-ordered cut drops.
    /// </summary>
    private const string NoisyErrors =
        """
        using Missing.Ns.One;
        using Missing.Ns.Two;

        namespace Demo
        {
            public interface IMany { void A(); void B(); void C(); }
            public sealed class NotImpl : IMany { }

            public sealed class NoBody { public void M1(); public void M2(); }

            public sealed class BadOverride { public override void N1() { } public override void N2() { } }

            public sealed class DupMethod1 { public void D(int x) { } public void D(int x) { } }
            public sealed class DupMethod2 { public void E(int x) { } public void E(int x) { } }

            public sealed class DupField1 { public int F; public int F; }
            public sealed class DupField2 { public int G; public int G; }

            public sealed class MissingUsers { public MissingType P; public MissingType Q; }

            public sealed class Twice1 { }
            public sealed class Twice1 { }

            public sealed class Twice2 { }
            public sealed class Twice2 { }

            public class BaseV { protected virtual void V() { } }
            public sealed class DerivedV1 : BaseV { public override void V() { } }
            public class BaseW { protected virtual void W() { } }
            public sealed class DerivedW1 : BaseW { public override void W() { } }

            public sealed class P1 { }
            public partial class P1 { }
            public sealed class P2 { }
            public partial class P2 { }

            public partial class Gen { public partial void Run(); }
        }
        """;

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

    private void Index() => Capture(() => IndexCommand.Execute(_root, new Options([])));

    private string Doctor() => Capture(() => DoctorCommand.Execute(_root, new Options([])));

    [Fact]
    public void A_project_with_CS8795_gets_one_warning_with_its_path_the_count_and_the_fix()
    {
        Write("Lib/Lib.csproj", Csproj);
        Write("Lib/Client.cs", "namespace Lib; public partial class Client { public partial void Call(); }");

        Index();
        var output = Doctor();

        Assert.Contains("Lib/Lib.csproj", output, StringComparison.Ordinal);
        Assert.Contains("1 CS8795", output, StringComparison.Ordinal);
        Assert.Contains("EmitCompilerGeneratedFiles=true", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_project_without_CS8795_gets_no_warning()
    {
        Write("Lib/Lib.csproj", Csproj);
        Write("Lib/Client.cs", "namespace Lib; public sealed class Client { }");

        Index();
        var output = Doctor();

        Assert.DoesNotContain("CS8795", output, StringComparison.Ordinal);
        Assert.DoesNotContain("EmitCompilerGeneratedFiles", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-project cut keeps eight ids by count, so one CS8795 loses to a project whose real
    /// errors repeat. It has to survive anyway: this project carries eight other ids, each twice,
    /// and a single CS8795. Before the exemption, the cut kept the eight and dropped the generator.
    /// </summary>
    [Fact]
    public void CS8795_survives_the_per_project_top_eight_cut()
    {
        Write("Lib/Lib.csproj", CsprojNoImplicitUsings);
        Write("Lib/Errors.cs", NoisyErrors);

        Index();
        var output = Doctor();

        Assert.Contains("Lib/Lib.csproj", output, StringComparison.Ordinal);
        Assert.Contains("1 CS8795", output, StringComparison.Ordinal);
        Assert.Contains("EmitCompilerGeneratedFiles=true", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_report_carries_the_project_and_the_count()
    {
        Write("Lib/Lib.csproj", Csproj);
        Write("Lib/Client.cs", "namespace Lib; public partial class Client { public partial void Call(); }");

        Index();
        var raw = Capture(() => DoctorCommand.Execute(_root, new Options(["--json"])));
        var report = System.Text.Json.JsonSerializer.Deserialize(
            raw.Trim(), AppJsonContext.Default.DoctorReport)!;

        var finding = Assert.Single(report.MissingGeneratedOutput);
        Assert.Equal("Lib/Lib.csproj", finding.Project);
        Assert.Equal(1, finding.Count);
    }
}
