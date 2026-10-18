using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Radiodex.Services.Sources;

/// <summary>Reads .m3u, .pls and .asx playlists and returns the stream URLs inside.</summary>
public static class Playlist
{
    private static readonly Regex PlsEntry = new(@"^\s*File\d+\s*=\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex AsxEntry = new(@"<ref\s+href\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);

    public static bool LooksLikePlaylistUrl(string url)
    {
        var path = url.Split('?', '#')[0].ToLowerInvariant();
        return path.EndsWith(".m3u") || path.EndsWith(".pls") || path.EndsWith(".asx") || path.EndsWith(".xspf");
    }

    /// <summary>True for HLS manifests, which are streams in themselves.</summary>
    public static bool IsHls(string text) => text.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase);

    public static List<string> Parse(string text)
    {
        var urls = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return urls;

        foreach (Match m in PlsEntry.Matches(text)) urls.Add(m.Groups[1].Value.Trim());
        foreach (Match m in AsxEntry.Matches(text)) urls.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim()));

        if (urls.Count == 0 && !IsHls(text))
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    urls.Add(line);
            }
        }

        return urls
            .Where(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<List<string>> FetchAsync(string url, CancellationToken ct, string? referer = null)
    {
        var text = await Http.GetStringAsync(url, ct, referer).ConfigureAwait(false);
        return Parse(text);
    }
}
