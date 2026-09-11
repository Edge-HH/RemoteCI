using System.Reflection;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>服务端自身版本，读取自 csproj 的 &lt;Version&gt;。</summary>
public static class AppVersion
{
    public static string Version { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+', 2)[0] ?? "0.0.0";

    public static PeerCapabilities Capabilities() => new()
    {
        SoftwareVersion = Version,
        Capabilities = RemoteCiCapabilities.Current,
    };
}
