using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Radiodex.Models;

namespace Radiodex.Services.Sources;

public sealed class RefreshResult
{
    public bool Found { get; init; }
    public bool Changed { get; init; }
    public string? StreamUrl { get; init; }
    public string? Codec { get; init; }
    public int Bitrate { get; init; }
}

/// <summary>
/// Turns a pasted link into a station, and finds a fresh stream link when one stops working.
/// Each source's rules are documented in docs/sources.md.
/// </summary>
public static class StationResolver
{
    private static readonly Regex RadiosComBrId = new(@"radios\.com\.br/(?:aovivo|play)/[^/?#]+/(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex RadioGardenId = new(@"radio\.garden/(?:listen|visit)/[^?#]*?/([A-Za-z0-9_-]{6,})(?:[/?#]|$)", RegexOptions.IgnoreCase);
    private static readonly Regex MetaTag = new(@"<meta\s+[^>]*(?:property|name)\s*=\s*""(?<k>[^""]+)""[^>]*content\s*=\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase);
    private static readonly Regex MetaTagReversed = new(@"<meta\s+[^>]*content\s*=\s*""(?<v>[^""]*)""[^>]*(?:property|name)\s*=\s*""(?<k>[^""]+)""", RegexOptions.IgnoreCase);
    private static readonly Regex StreamInHtml = new(@"https?://[^\s""'<>\\]+?(?:\.mp3|\.aac|\.aacp|\.ogg|\.m3u8|/stream|/live|/;)(?:\?[^\s""'<>\\]*)?(?=[\s""'<>\\])", RegexOptions.IgnoreCase);

    public static StationSource Detect(string link)
    {
        if (RadiosComBrId.IsMatch(link)) return StationSource.RadiosComBr;
        if (RadioGardenId.IsMatch(link)) return StationSource.RadioGarden;
        return StationSource.Direct;
    }

    public static string SourceName(StationSource s) => s switch
    {
        StationSource.RadiosComBr => "radios.com.br",
        StationSource.RadioGarden => "Radio Garden",
        StationSource.RadioBrowser => "Radio Browser",
        _ => Loc.I.Language == "pt" ? "Link direto" : "Direct link"
    };

    public static bool LooksLikeLink(string text)
    {
        var t = text.Trim();
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("radios.com.br", StringComparison.OrdinalIgnoreCase) ||
               t.Contains("radio.garden", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string link)
    {
        var t = link.Trim();
        if (!t.StartsWith("http", StringComparison.OrdinalIgnoreCase)) t = "https://" + t.TrimStart('/');
        return t;
    }

    /// <summary>Resolves any supported link into a station candidate with a working stream URL.</summary>
    public static async Task<StationCandidate?> ResolveLinkAsync(string link, CancellationToken ct)
    {
        link = Normalize(link);
        return Detect(link) switch
        {
            StationSource.RadiosComBr => await RadiosComBrAsync(link, ct).ConfigureAwait(false),
            StationSource.RadioGarden => await RadioGardenAsync(link, ct).ConfigureAwait(false),
            _ => await DirectAsync(link, ct).ConfigureAwait(false)
        };
    }

    // ---------- radios.com.br ----------
    // The page loads its player with JavaScript, but every station has public playlist
    // files at /play/playlist/{id}/listen-radio.(m3u|pls|asx) that list the stream.
    private static async Task<StationCandidate?> RadiosComBrAsync(string link, CancellationToken ct)
    {
        var id = RadiosComBrId.Match(link).Groups[1].Value;
        string? name = null, logo = null, place = null, html = null;

        try
        {
            html = await Http.GetStringAsync(link, ct).ConfigureAwait(false);
            var meta = ReadMeta(html);
            meta.TryGetValue("og:title", out name);
            meta.TryGetValue("og:image", out logo);
            if (meta.TryGetValue("og:description", out var desc)) place = GuessPlace(desc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* playlists may still work */ }

        var urls = new List<string>();
        foreach (var ext in new[] { "m3u", "pls", "asx" })
        {
            try
            {
                urls.AddRange(await Playlist.FetchAsync($"https://www.radios.com.br/play/playlist/{id}/listen-radio.{ext}", ct, link).ConfigureAwait(false));
                if (urls.Count > 0) break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        if (html != null) urls.AddRange(StreamInHtml.Matches(html).Select(m => WebUtility.HtmlDecode(m.Value)));

        var stream = await FirstWorkingAsync(urls, ct).ConfigureAwait(false);
        if (stream == null) return null;

        return new StationCandidate
        {
            Name = CleanName(name) ?? stream.IcyName ?? "radios.com.br " + id,
            LogoUrl = logo,
            Place = place,
            StreamUrl = stream.FinalUrl ?? urls[0],
            Codec = StreamProbe.CodecFrom(stream.ContentType),
            Bitrate = stream.Bitrate,
            Source = StationSource.RadiosComBr,
            SourceRef = link
        };
    }

    // ---------- Radio Garden ----------
    // Links look like radio.garden/listen/{slug}/{channelId}. Details come from the
    // site's channel API and the stream from its "listen" endpoint, which redirects
    // to the station's own server. This API is not officially documented.
    private static async Task<StationCandidate?> RadioGardenAsync(string link, CancellationToken ct)
    {
        var id = RadioGardenId.Match(link).Groups[1].Value;
        string? name = null, place = null;
        try
        {
            var json = await Http.GetStringAsync($"https://radio.garden/api/ara/content/channel/{id}", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : doc.RootElement;
            if (data.TryGetProperty("title", out var t)) name = t.GetString();
            var city = data.TryGetProperty("place", out var p) && p.TryGetProperty("title", out var pt) ? pt.GetString() : null;
            var country = data.TryGetProperty("country", out var c) && c.TryGetProperty("title", out var ctt) ? ctt.GetString() : null;
            place = string.Join(" · ", new[] { city, country }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var listen = $"https://radio.garden/api/ara/content/listen/{id}/channel.mp3";
        var target = listen;
        try
        {
            using var resp = await Http.NoRedirect.GetAsync(listen, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var loc = resp.Headers.Location;
            if (loc != null) target = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(listen), loc).ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var probe = await StreamProbe.ProbeAsync(target, ct).ConfigureAwait(false);
        if (probe.Result == ProbeResult.Failed && target != listen)
        {
            target = listen;
            probe = await StreamProbe.ProbeAsync(target, ct).ConfigureAwait(false);
        }
        if (probe.Result == ProbeResult.Failed) return null;

        return new StationCandidate
        {
            Name = name ?? probe.IcyName ?? "Radio Garden " + id,
            Place = string.IsNullOrWhiteSpace(place) ? null : place,
            StreamUrl = probe.FinalUrl ?? target,
            Codec = StreamProbe.CodecFrom(probe.ContentType),
            Bitrate = probe.Bitrate,
            Source = StationSource.RadioGarden,
            SourceRef = link
        };
    }

    // ---------- Direct stream or playlist ----------
    private static async Task<StationCandidate?> DirectAsync(string link, CancellationToken ct)
    {
        var urls = new List<string>();
        if (Playlist.LooksLikePlaylistUrl(link))
        {
            try { urls.AddRange(await Playlist.FetchAsync(link, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        urls.Add(link);

        var stream = await FirstWorkingAsync(urls, ct).ConfigureAwait(false);
        if (stream == null)
        {
            // Maybe a normal web page that embeds a player: look for stream links in it.
            try
            {
                var html = await Http.GetStringAsync(link, ct).ConfigureAwait(false);
                var meta = ReadMeta(html);
                var found = StreamInHtml.Matches(html).Select(m => WebUtility.HtmlDecode(m.Value)).ToList();
                stream = await FirstWorkingAsync(found, ct).ConfigureAwait(false);
                if (stream == null) return null;
                meta.TryGetValue("og:title", out var title);
                meta.TryGetValue("og:image", out var img);
                return new StationCandidate
                {
                    Name = CleanName(title) ?? stream.IcyName ?? new Uri(link).Host,
                    LogoUrl = img,
                    StreamUrl = stream.FinalUrl ?? found[0],
                    Codec = StreamProbe.CodecFrom(stream.ContentType),
                    Bitrate = stream.Bitrate,
                    Source = StationSource.Direct,
                    SourceRef = link
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return null;
            }
        }

        return new StationCandidate
        {
            Name = stream.IcyName?.Trim() is { Length: > 0 } n ? n : new Uri(link).Host,
            StreamUrl = stream.FinalUrl ?? link,
            Codec = StreamProbe.CodecFrom(stream.ContentType),
            Bitrate = stream.Bitrate,
            Source = StationSource.Direct,
            SourceRef = link
        };
    }

    /// <summary>
    /// Called when a station stops playing (or the user taps "Refresh link"):
    /// 1) ask the station's own source again, 2) keep the current link if it still works,
    /// 3) look the station up by name in Radio Browser.
    /// </summary>
    public static async Task<RefreshResult> RefreshAsync(Station station, CancellationToken ct)
    {
        StationCandidate? fromSource = null;
        try
        {
            fromSource = station.Source switch
            {
                StationSource.RadioBrowser when station.SourceRef != null =>
                    await RadioBrowser.ByUuidAsync(station.SourceRef, ct).ConfigureAwait(false),
                StationSource.RadiosComBr or StationSource.RadioGarden when station.SourceRef != null =>
                    await ResolveLinkAsync(station.SourceRef, ct).ConfigureAwait(false),
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        if (fromSource != null && !SameUrl(fromSource.StreamUrl, station.StreamUrl))
        {
            var p = await StreamProbe.ProbeAsync(fromSource.StreamUrl, ct).ConfigureAwait(false);
            if (p.Result != ProbeResult.Failed) return Changed(fromSource.StreamUrl, fromSource.Codec, fromSource.Bitrate);
        }

        var current = await StreamProbe.ProbeAsync(station.StreamUrl, ct).ConfigureAwait(false);
        if (current.Result == ProbeResult.Ok) return new RefreshResult { Found = true, Changed = false, StreamUrl = station.StreamUrl };

        // Last resort: the same station in the open catalog.
        try
        {
            var matches = await RadioBrowser.SearchAsync(station.Name, null, ct).ConfigureAwait(false);
            var target = Simplify(station.Name);
            foreach (var m in matches.Where(m => Simplify(m.Name) == target).Take(5))
            {
                if (SameUrl(m.StreamUrl, station.StreamUrl)) continue;
                var p = await StreamProbe.ProbeAsync(m.StreamUrl, ct).ConfigureAwait(false);
                if (p.Result == ProbeResult.Ok) return Changed(m.StreamUrl, m.Codec, m.Bitrate);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        if (current.Result == ProbeResult.Unknown) return new RefreshResult { Found = true, Changed = false, StreamUrl = station.StreamUrl };
        return new RefreshResult { Found = false };
    }

    private static RefreshResult Changed(string url, string? codec, int bitrate) =>
        new() { Found = true, Changed = true, StreamUrl = url, Codec = codec, Bitrate = bitrate };

    private static async Task<ProbeInfo?> FirstWorkingAsync(IEnumerable<string> urls, CancellationToken ct)
    {
        ProbeInfo? unknown = null;
        foreach (var u in urls.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            var p = await StreamProbe.ProbeAsync(u, ct).ConfigureAwait(false);
            if (p.Result == ProbeResult.Ok) return p;
            if (p.Result == ProbeResult.Unknown && unknown == null) unknown = new ProbeInfo { Result = ProbeResult.Unknown, FinalUrl = u };
        }
        return unknown;
    }

    private static Dictionary<string, string> ReadMeta(string html)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rx in new[] { MetaTag, MetaTagReversed })
            foreach (Match m in rx.Matches(html))
            {
                var k = m.Groups["k"].Value;
                if (!meta.ContainsKey(k)) meta[k] = WebUtility.HtmlDecode(m.Groups["v"].Value).Trim();
            }
        return meta;
    }

    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var n = name!.Trim();
        foreach (var sep in new[] { " | ", " - Ouvir", " - Ao vivo", " – " })
        {
            var i = n.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) n = n.Substring(0, i);
        }
        return n.Trim();
    }

    private static string? GuessPlace(string description)
    {
        // e.g. "... de Belo Horizonte / MG - Brasil ..."
        var m = Regex.Match(description, @"([A-ZÀ-Ú][\p{L} ]+ / [A-Z]{2})(?: - ([\p{L} ]+))?");
        return m.Success ? m.Value.Trim() : null;
    }

    private static bool SameUrl(string a, string b) =>
        string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string Simplify(string name)
    {
        var chars = name.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD)
            .Where(ch => char.IsLetterOrDigit(ch));
        return new string(chars.ToArray());
    }
}
