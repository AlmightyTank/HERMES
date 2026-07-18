using System.Reflection;

namespace Hermes.Client;

internal static class HermesVersionInfo
{
    internal static string DisplayVersion { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(HermesVersionInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return Plugin.PluginVersion;
        }

        var metadataIndex = informational.IndexOf('+');
        return metadataIndex >= 0
            ? informational[..metadataIndex]
            : informational;
    }
}
