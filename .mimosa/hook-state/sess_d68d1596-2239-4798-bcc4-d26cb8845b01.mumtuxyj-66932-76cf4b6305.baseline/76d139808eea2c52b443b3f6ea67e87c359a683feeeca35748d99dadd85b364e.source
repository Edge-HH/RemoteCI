using System.Text;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using System.Text.Json;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class LanDiscoveryProtocolTests
{
    [Fact]
    public void DiscoveryRequest_ReturnsSelectablePluginEndpointOnly()
    {
        var settings = new PluginSettings
        {
            LanServerPort = 9123,
            CloudServerUrl = "https://cloud.example.com",
        };

        var response = LanDiscoveryProtocol.CreateResponse(
            Encoding.UTF8.GetBytes(Protocol.LanDiscoveryRequest),
            settings,
            "Classroom-PC");

        Assert.NotNull(response);
        Assert.Equal(Protocol.Version, response.ProtocolVersion);
        Assert.Equal("Classroom-PC", response.InstanceName);
        Assert.Equal(9123, response.Port);
    }

    [Fact]
    public void SelectedPlugin_ReturnsCloudBootstrapEnvelopeWithoutAuthenticationData()
    {
        var settings = new PluginSettings { CloudServerUrl = " https://cloud.example.com/ " };

        var envelope = LanDiscoveryProtocol.CreateBootstrapEnvelope(settings, "Classroom-PC");
        var bootstrap = JsonSerializer.Deserialize<ConnectionBootstrapInfo>(
            JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;

        Assert.Equal(Protocol.MessageTypeConnectionBootstrap, envelope.Type);
        Assert.Equal("https://cloud.example.com", bootstrap.CloudServerUrl);
        Assert.Equal("Classroom-PC", bootstrap.InstanceName);
    }
}
