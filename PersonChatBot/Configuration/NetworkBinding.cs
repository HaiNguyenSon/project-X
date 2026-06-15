using System.Net;

namespace PersonChatBot.Configuration;

/// <summary>
/// Works out whether the app is bound only to loopback (localhost). This decides
/// whether it is safe to trust X-Forwarded-* headers from any source: behind
/// `tailscale serve` the only client is the local proxy, but if the app is bound
/// to a public address those headers can be spoofed.
/// </summary>
public static class NetworkBinding
{
    public static bool IsLoopbackOnly(IConfiguration configuration)
    {
        var urls = CollectBindingUrls(configuration);

        // No explicit binding configured: ASP.NET Core defaults to localhost.
        if (urls.Count == 0)
            return true;

        return urls.All(IsLoopback);
    }

    private static List<string> CollectBindingUrls(IConfiguration configuration)
    {
        var urls = new List<string>();

        // From ASPNETCORE_URLS / --urls (surfaced by the host as the "urls" key).
        var urlsKey = configuration["urls"] ?? configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(urlsKey))
            urls.AddRange(urlsKey.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        // From Kestrel:Endpoints:*:Url in configuration.
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url);
        }

        return urls;
    }

    private static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        // Wildcard / any-address bindings are reachable from the network.
        if (host is "*" or "+" or "0.0.0.0" or "::" or "[::]")
            return false;

        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }
}
