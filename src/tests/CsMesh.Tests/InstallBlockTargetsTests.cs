using CsMesh.Commands;
using CsMesh.Common;
using CsMesh.Skill;
using Xunit;

namespace CsMesh.Tests;

/// <summary>
/// doctor decides which files to compare from <see cref="SkillCommand.BlockTargets"/>, while install
/// decides what to write from its per-agent Install* methods. Two hand-maintained lists that must
/// agree will eventually not, and the dangerous direction is a block target added to install but not
/// to BlockTargets: doctor then never compares it, which is the one place a stale block would have
/// been caught. This runs the real install path for both scopes and asserts that the files actually
/// carrying a block are exactly the files BlockTargets names.
/// </summary>
[Collection("console-capture")]
public sealed class InstallBlockTargetsTests : IDisposable
{
    private readonly string _root;
    private readonly string _home;
    private readonly Dictionary<string, string?> _saved = new();

    public InstallBlockTargetsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "csmesh-targets-" + Guid.NewGuid().ToString("N")[..8]);
        _home = Path.Combine(Path.GetTempPath(), "csmesh-targets-home-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_home);

        // BlockTargets and the global Install* methods both resolve these from the environment. Pin
        // them inside the temp home so the test never touches the real user configuration and both
        // sides resolve the same paths.
        SetEnv("CLAUDE_CONFIG_DIR", Path.Combine(_home, ".claude"));
        SetEnv("COPILOT_HOME", Path.Combine(_home, ".copilot"));
        SetEnv("CODEX_HOME", Path.Combine(_home, ".codex"));
        SetEnv("XDG_CONFIG_HOME", Path.Combine(_home, ".config"));
    }

    private void SetEnv(string name, string value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
        try { Directory.Delete(_home, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public void EveryInstalledBlockFileIsListedInBlockTargets()
    {
        Assert.Equal(Exit.Ok, SkillCommand.Install(_root, "all", isGlobal: false));
        Assert.Equal(Exit.Ok, SkillCommand.Install(_home, "all", isGlobal: true));

        var installed = Directory
            .EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(_home, "*", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains(SkillBlock.StartTag, StringComparison.Ordinal))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var listed = SkillCommand.BlockTargets(_root, isGlobal: false)
            .Concat(SkillCommand.BlockTargets(_home, isGlobal: true))
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            listed.Order(StringComparer.OrdinalIgnoreCase),
            installed.Order(StringComparer.OrdinalIgnoreCase));
    }
}
