using Microsoft.Extensions.Configuration;
using PersonChatBot.Configuration;

namespace PersonChatBot.Tests;

public class NetworkBindingTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void No_binding_configured_is_treated_as_loopback()
        => Assert.True(NetworkBinding.IsLoopbackOnly(Config([])));

    [Theory]
    [InlineData("http://localhost:5088")]
    [InlineData("http://127.0.0.1:5088")]
    [InlineData("http://[::1]:5088")]
    public void Loopback_urls_are_loopback(string url)
        => Assert.True(NetworkBinding.IsLoopbackOnly(Config(new() { ["urls"] = url })));

    [Theory]
    [InlineData("http://0.0.0.0:5088")]
    [InlineData("http://*:5088")]
    [InlineData("http://192.168.1.50:5088")]
    public void Public_urls_are_not_loopback(string url)
        => Assert.False(NetworkBinding.IsLoopbackOnly(Config(new() { ["urls"] = url })));

    [Fact]
    public void Mixed_binding_is_not_loopback()
        => Assert.False(NetworkBinding.IsLoopbackOnly(
            Config(new() { ["urls"] = "http://localhost:5088;http://0.0.0.0:8080" })));

    [Fact]
    public void Kestrel_endpoint_url_is_considered()
    {
        Assert.True(NetworkBinding.IsLoopbackOnly(
            Config(new() { ["Kestrel:Endpoints:Http:Url"] = "http://localhost:5088" })));
        Assert.False(NetworkBinding.IsLoopbackOnly(
            Config(new() { ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:5088" })));
    }
}
