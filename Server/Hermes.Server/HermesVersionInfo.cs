using System.Reflection;

namespace Hermes.Server;

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
            return "1.2.0";
        }

        var metadataIndex = informational.IndexOf('+');
        return metadataIndex >= 0
            ? informational[..metadataIndex]
            : informational;
    }
}
