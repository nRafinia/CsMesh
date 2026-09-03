using System.Reflection;

namespace CsMesh.Common;

public static class AppVersion
{
    public static string Get()
    {
        var asm = Assembly.GetEntryAssembly();
        var info = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }

        var version = asm?.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}" : "0.0.1";
    }
}