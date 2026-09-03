using System.Reflection;

namespace CsMesh.Common;

public static class AppVersion
{
    public static string Get()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var appVersion = version?.ToString() ?? "0.0.0.0";
        return appVersion;
    }
}