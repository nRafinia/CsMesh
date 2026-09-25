using System.Text.Json;
using CsMesh.Commands;
using CsMesh.Common;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// A PackageReference whose Include names a project in the solution is never restored, so the
/// referenced project's types are not bound through it and every call into them is unbound -- while
/// the sources sit one element away from the ProjectReference that would bind them. The match is
/// read from csproj XML only, so Condition attributes are ignored, consistent with ProjectScope.
/// </summary>
[Collection("console-capture")]
public sealed class PackageReferenceToProjectWarningTests : IDisposable
{
    private readonly string _root;

    public PackageReferenceToProjectWarningTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-pkgproj-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private static string Csproj(string body = "") =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework>"
        + body + "</PropertyGroup></Project>";

    private static string Consumer(params string[] packageIds) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
        + "<ItemGroup>" + string.Concat(packageIds.Select(id => $"<PackageReference Include=\"{id}\" />")) + "</ItemGroup></Project>";

    private static string Solution(params string[] paths) =>
        "<Solution>" + string.Concat(paths.Select(p => $"<Project Path=\"{p}\" />")) + "</Solution>";

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
    public void A_package_reference_to_an_in_scope_project_warns_naming_both()
    {
        Write("A/A.csproj", Csproj());
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("B/B.csproj", Consumer("A"));
        Write("B/Use.cs", "namespace B; public sealed class Use { }");
        Write("App.slnx", Solution("A/A.csproj", "B/B.csproj"));

        Index();
        var output = Doctor();

        Assert.Contains("package ref     A/A.csproj", output, StringComparison.Ordinal);
        Assert.Contains("B/B.csproj", output, StringComparison.Ordinal);
        Assert.Contains("PackageReference 'A'", output, StringComparison.Ordinal);
        Assert.Contains("ProjectReference", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_package_id_is_matched_instead_of_the_file_name()
    {
        Write("A/A.csproj", Csproj("<PackageId>Company.Contracts</PackageId>"));
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("B/B.csproj", Consumer("Company.Contracts"));
        Write("B/Use.cs", "namespace B; public sealed class Use { }");
        Write("App.slnx", Solution("A/A.csproj", "B/B.csproj"));

        Index();
        var output = Doctor();

        Assert.Contains("package ref     A/A.csproj", output, StringComparison.Ordinal);
        Assert.Contains("Company.Contracts", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_package_reference_to_a_package_or_an_out_of_scope_project_is_not_warned_about()
    {
        Write("A/A.csproj", Csproj());
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("B/B.csproj", Consumer("Newtonsoft.Json", "V"));
        Write("B/Use.cs", "namespace B; public sealed class Use { }");

        // V is a project on disk with the package id its file name gives it, but the solution does
        // not list it, so it is out of scope and the reference to it is not this warning's subject.
        Write("Vendored/V.csproj", Csproj());
        Write("Vendored/V.cs", "namespace V; public sealed class V { }");
        Write("App.slnx", Solution("A/A.csproj", "B/B.csproj"));

        Index();
        var output = Doctor();

        Assert.DoesNotContain("package ref", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_report_carries_both_projects_and_the_id()
    {
        Write("A/A.csproj", Csproj());
        Write("A/Thing.cs", "namespace A; public sealed class Thing { }");
        Write("B/B.csproj", Consumer("A"));
        Write("B/Use.cs", "namespace B; public sealed class Use { }");
        Write("App.slnx", Solution("A/A.csproj", "B/B.csproj"));

        Index();
        var raw = Capture(() => DoctorCommand.Execute(_root, new Options(["--json"])));
        var report = JsonSerializer.Deserialize(raw.Trim(), AppJsonContext.Default.DoctorReport)!;

        var finding = Assert.Single(report.PackageReferencesToProjects);
        Assert.Equal("B/B.csproj", finding.ReferencedFrom);
        Assert.Equal("A", finding.PackageId);
        Assert.Equal("A/A.csproj", finding.ReferencedProject);
    }
}
