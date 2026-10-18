using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Radiodex.Services.Sources;

public static class Http
{
    public const string UserAgent = "Radiodex/0.1 (+https://github.com/filipembcruz/radiodex)";

    /// <summary>For pages and APIs. Follows redirects.</summary>
    public static HttpClient Client { get; } = Create(followRedirects: true, TimeSpan.FromSeconds(20));

    /// <summary>For reading where a redirect points without following it.</summary>
    public static HttpClient NoRedirect { get; } = Create(followRedirects: false, TimeSpan.FromSeconds(15));

    /// <summary>For long-lived audio connections (no overall timeout).</summary>
    public static HttpClient Streaming { get; } = Create(followRedirects: true, Timeout.InfiniteTimeSpan);

    private static HttpClient Create(bool followRedirects, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var c = new HttpClient(handler) { Timeout = timeout };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct, string? referer = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.Referrer = new Uri(referer);
        using var resp = await Client.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }
}
