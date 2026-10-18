using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Radiodex.Services.Sources;

public enum ProbeResult { Ok, Failed, Unknown }

public sealed class ProbeInfo
{
    public ProbeResult Result { get; init; }
    public string? ContentType { get; init; }
    public string? IcyName { get; init; }
    public int Bitrate { get; init; }
    public string? FinalUrl { get; init; }
}

/// <summary>Opens a stream for a moment to see whether it answers with audio.</summary>
public static class StreamProbe
{
    public static async Task<ProbeInfo> ProbeAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            using var resp = await Http.Streaming.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new ProbeInfo { Result = ProbeResult.Failed };

            var type = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            string? Header(string name) =>
                resp.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() :
                resp.Content.Headers.TryGetValues(name, out var v2) ? v2.FirstOrDefault() : null;

            int.TryParse(Header("icy-br")?.Split(',')[0], out var br);

            // Read a few bytes: a live stream sends data right away.
            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var buf = new byte[2048];
            var n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
            if (n <= 0) return new ProbeInfo { Result = ProbeResult.Failed };

            var isText = type != null && (type.StartsWith("text/html") || type == "application/json");
            return new ProbeInfo
            {
                Result = isText ? ProbeResult.Failed : ProbeResult.Ok,
                ContentType = type,
                IcyName = Header("icy-name"),
                Bitrate = br,
                FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeInfo { Result = ProbeResult.Failed };
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse ||
                                               ex.Message.Contains("ICY", StringComparison.OrdinalIgnoreCase) ||
                                               ex.InnerException?.Message.Contains("ICY", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Old SHOUTcast v1 servers answer "ICY 200 OK", which HttpClient rejects
            // but Windows' player accepts. We can't tell, so let the player try.
            return new ProbeInfo { Result = ProbeResult.Unknown };
        }
        catch (HttpRequestException)
        {
            return new ProbeInfo { Result = ProbeResult.Failed };
        }
    }

    /// <summary>Guess the codec from a content type.</summary>
    public static string? CodecFrom(string? contentType) => contentType switch
    {
        null => null,
        var t when t.Contains("mpeg") && !t.Contains("url") => "mp3",
        var t when t.Contains("aac") => "aac",
        var t when t.Contains("ogg") => "ogg",
        var t when t.Contains("opus") => "opus",
        var t when t.Contains("flac") => "flac",
        var t when t.Contains("mpegurl") => "hls",
        _ => null
    };
}
