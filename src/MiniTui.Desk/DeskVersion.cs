using System.Reflection;

namespace MiniTui.Desk;

internal static class DeskVersion
{
    /// <summary>The package version, without the commit the build adds after a <c>+</c>.</summary>
    public static string Current { get; } =
        typeof(DeskVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";
}
