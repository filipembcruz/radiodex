using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Radiodex.Models;

namespace Radiodex.Services.Sources;

/// <summary>
/// Radio Browser (https://www.radio-browser.info): a free, open, community catalog
/// of internet radio stations with a public API.
/// </summary>
public static class RadioBrowser
{
    private static readonly string[] Servers =
    {
        "https://de1.api.radio-browser.info",
        "https://de2.api.radio-browser.info",
        "https://nl1.api.radio-browser.info",
        "https://at1.api.radio-browser.info",
        "https://fi1.api.radio-browser.info",
        "https://all.api.radio-browser.info"
    };

    private static int _preferred;

    private static async Task<JsonDocument> GetJsonAsync(string pathAndQuery, CancellationToken ct)
    {
        Exception? last = null;
        for (var i = 0; i < Servers.Length; i++)
        {
            var idx = (_preferred + i) % Servers.Length;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var resp = await Http.Client.GetAsync(Servers[idx] + pathAndQuery, cts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);
                _preferred = idx;
                return doc;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (ct.IsCancellationRequested) throw;
                last = ex;
            }
        }
        throw new HttpRequestException("Radio Browser is unreachable", last);
    }

    public static async Task<List<StationCandidate>> SearchAsync(string query, string? countryCode, CancellationToken ct)
    {
        var q = Uri.EscapeDataString(query.Trim());
        var cc = string.IsNullOrWhiteSpace(countryCode) ? "" : "&countrycode=" + Uri.EscapeDataString(countryCode!);
        const string common = "&limit=40&hidebroken=true&order=clickcount&reverse=true";

        var results = new List<StationCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Name first; then city/state and genre so "Belo Horizonte" or "jazz" also work.
        foreach (var field in new[] { "name", "state", "tag" })
        {
            using var doc = await GetJsonAsync($"/json/stations/search?{field}={q}{cc}{common}", ct).ConfigureAwait(false);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var c = ToCandidate(el);
                if (c == null || !seen.Add(c.SourceRef ?? c.StreamUrl)) continue;
                results.Add(c);
            }
            if (results.Count >= 25) break;
        }
        return results;
    }

    public static async Task<StationCandidate?> ByUuidAsync(string uuid, CancellationToken ct)
    {
        using var doc = await GetJsonAsync("/json/stations/byuuid/" + Uri.EscapeDataString(uuid), ct).ConfigureAwait(false);
        foreach (var el in doc.RootElement.EnumerateArray())
            return ToCandidate(el);
        return null;
    }

    /// <summary>Tells the catalog a station was played (helps its popularity ranking).</summary>
    public static void CountClick(string uuid)
    {
        _ = Task.Run(async () =>
        {
            try { using var _d = await GetJsonAsync("/json/url/" + Uri.EscapeDataString(uuid), CancellationToken.None).ConfigureAwait(false); }
            catch { /* not important */ }
        });
    }

    private static StationCandidate? ToCandidate(JsonElement el)
    {
        string? S(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int N(string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

        var url = S("url_resolved");
        if (string.IsNullOrWhiteSpace(url)) url = S("url");
        var name = S("name")?.Trim();
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(name)) return null;

        var place = string.Join(" · ", new[] { S("state"), S("country") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var favicon = S("favicon");
        return new StationCandidate
        {
            Name = name!,
            StreamUrl = url!,
            LogoUrl = favicon != null && favicon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? favicon : null,
            Place = place.Length > 0 ? place : null,
            Codec = S("codec")?.ToLowerInvariant(),
            Bitrate = N("bitrate"),
            Source = StationSource.RadioBrowser,
            SourceRef = S("stationuuid")
        };
    }
}
