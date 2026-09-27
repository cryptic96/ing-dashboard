using FluentAssertions;
using Ledger.Service.Hosting;
using Microsoft.Extensions.Configuration;

namespace Ledger.UnitTests.Hosting;

/// <summary>Verifies the ops endpoint only accepts loopback hosts.</summary>
public class OpsEndpointTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5081", 5081)]
    [InlineData("http://[::1]:5081", 5081)]
    [InlineData("http://localhost:5081", 5081)]
    public void FromConfiguration_accepts_loopback_hosts(string url, int expectedPort)
    {
        var configuration = BuildConfiguration(url);

        var port = OpsEndpoint.FromConfiguration(configuration);

        port.Should().Be(expectedPort);
    }

    [Theory]
    [InlineData("http://0.0.0.0:5081")]
    [InlineData("http://*:5081")]
    [InlineData("http://+:5081")]
    [InlineData("http://192.0.2.10:5081")]
    public void FromConfiguration_rejects_non_loopback_hosts(string url)
    {
        var configuration = BuildConfiguration(url);

        var act = () => OpsEndpoint.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void FromConfiguration_rejects_missing_url()
    {
        var configuration = BuildConfiguration(null);

        var act = () => OpsEndpoint.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Kestrel:Endpoints:Ops:Url*");
    }

    private static IConfiguration BuildConfiguration(string? opsUrl)
    {
        var values = new Dictionary<string, string?>();
        if (opsUrl is not null)
        {
            values["Kestrel:Endpoints:Ops:Url"] = opsUrl;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
