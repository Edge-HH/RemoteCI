using System.Reflection;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

internal static class PluginAppInfo
{
    public static string Version { get; } = typeof(PluginAppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+', 2)[0] ?? "0.0.0";

    public static PeerCapabilities Capabilities() => new()
    {
        SoftwareVersion = Version,
        Capabilities = RemoteCiCapabilities.Current,
    };
}
