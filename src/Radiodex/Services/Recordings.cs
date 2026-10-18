using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Radiodex.Services;

public sealed class CueTrack
{
    public TimeSpan Start { get; set; }
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
}

/// <summary>Reads and writes the .cue sheets Radiodex saves next to each recording.</summary>
public sealed class CueSheet
{
    private static readonly Regex Quoted = new("\"(.*)\"");
    private static readonly Regex Index = new(@"INDEX\s+01\s+(\d+):(\d{2}):(\d{2})", RegexOptions.IgnoreCase);

    public string Title { get; set; } = "";
    public string Performer { get; set; } = "";
    public string FileName { get; set; } = "";
    public List<CueTrack> Tracks { get; } = new();

    public static CueSheet? Load(string cuePath)
    {
        if (!File.Exists(cuePath)) return null;
        var sheet = new CueSheet();
        CueTrack? cur = null;
        foreach (var raw in File.ReadAllLines(cuePath, Encoding.UTF8))
        {
            var line = raw.Trim();
            var q = Quoted.Match(line);
            var value = q.Success ? q.Groups[1].Value : "";
            if (line.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                cur = new CueTrack();
                sheet.Tracks.Add(cur);
            }
            else if (line.StartsWith("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null) cur.Title = value; else sheet.Title = value;
            }
            else if (line.StartsWith("PERFORMER", StringComparison.OrdinalIgnoreCase))
            {
                if (cur != null) cur.Artist = value; else sheet.Performer = value;
            }
            else if (line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                sheet.FileName = value;
            }
            else if (cur != null)
            {
                var m = Index.Match(line);
                if (m.Success)
                {
                    var mm = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    var ss = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    var ff = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    cur.Start = TimeSpan.FromSeconds(mm * 60 + ss + ff / 75.0);
                }
            }
        }
        return sheet;
    }

    public void Save(string cuePath)
    {
        static string Q(string s) => s.Replace("\"", "'");
        var name = FileName.Length > 0 ? FileName : Path.ChangeExtension(Path.GetFileName(cuePath), ".mp3");
        var kind = name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "MP3" : "WAVE";
        var sb = new StringBuilder();
        if (Performer.Length > 0) sb.AppendLine($"PERFORMER \"{Q(Performer)}\"");
        if (Title.Length > 0) sb.AppendLine($"TITLE \"{Q(Title)}\"");
        sb.AppendLine($"FILE \"{Q(name)}\" {kind}");
        for (var i = 0; i < Tracks.Count; i++)
        {
            var t = Tracks[i];
            var frames = (long)Math.Round(Math.Max(0, t.Start.TotalSeconds) * 75);
            sb.AppendLine($"  TRACK {i + 1:00} AUDIO");
            sb.AppendLine($"    TITLE \"{Q(t.Title)}\"");
            if (!string.IsNullOrEmpty(t.Artist)) sb.AppendLine($"    PERFORMER \"{Q(t.Artist!)}\"");
            sb.AppendLine($"    INDEX 01 {frames / 4500:00}:{frames / 75 % 60:00}:{frames % 75:00}");
        }
        File.WriteAllText(cuePath, sb.ToString(), new UTF8Encoding(true));
    }
}

/// <summary>One saved recording in the recordings folder.</summary>
public sealed class RecordingFile
{
    private static readonly string[] AudioExt = { ".mp3", ".aac", ".ogg", ".flac" };

    public string AudioPath { get; init; } = "";
    public string CuePath => Path.ChangeExtension(AudioPath, ".cue");
    public string FileName => Path.GetFileName(AudioPath);
    public DateTime Created { get; init; }
    public long Size { get; init; }
    public int SongCount { get; set; }

    public string Station
    {
        get
        {
            // "{Station}_{yyyy-MM-dd_HH-mm-ss}.ext"
            var stem = Path.GetFileNameWithoutExtension(AudioPath);
            var m = Regex.Match(stem, @"^(.*)_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}(-\d{2})?$");
            return m.Success ? m.Groups[1].Value : stem;
        }
    }

    public string Details
    {
        get
        {
            var size = Size >= 1024 * 1024 ? $"{Size / 1024.0 / 1024.0:0.0} MB" : $"{Size / 1024.0:0} KB";
            var songs = SongCount > 0 ? " · ♪ " + SongCount : "";
            return $"{Created.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture)} · {size}{songs}";
        }
    }

    public static List<RecordingFile> Scan(string folder)
    {
        var list = new List<RecordingFile>();
        if (!Directory.Exists(folder)) return list;
        foreach (var f in Directory.EnumerateFiles(folder))
        {
            if (!AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            var info = new FileInfo(f);
            if (info.Length < 16 * 1024) continue;
            var rec = new RecordingFile { AudioPath = f, Created = info.CreationTime, Size = info.Length };
            try { rec.SongCount = CueSheet.Load(rec.CuePath)?.Tracks.Count ?? 0; } catch { }
            list.Add(rec);
        }
        return list.OrderByDescending(r => r.Created).ToList();
    }
}

/// <summary>
/// Cuts a recording into one file per song without re-encoding. Radio streams are constant
/// bitrate, so a time maps to a byte position; each cut is moved to the next MP3/AAC frame
/// boundary so every piece plays cleanly. MP3 pieces get an ID3 tag with title and artist.
/// </summary>
public static class TrackSplitter
{
    public sealed class Piece
    {
        public int Number { get; init; }
        public TimeSpan Start { get; init; }
        public TimeSpan End { get; init; }
        public string Title { get; init; } = "";
        public string? Artist { get; init; }
    }

    public static string Split(string audioPath, TimeSpan total, IReadOnlyList<Piece> pieces, string album)
    {
        var ext = Path.GetExtension(audioPath).ToLowerInvariant();
        var isMp3 = ext == ".mp3";
        var outDir = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath) + " - " + Loc.T("TracksFolder"));
        Directory.CreateDirectory(outDir);

        using var src = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = src.Length;
        var bytesPerSecond = length / Math.Max(1, total.TotalSeconds);

        foreach (var p in pieces)
        {
            var start = Align(src, (long)(p.Start.TotalSeconds * bytesPerSecond), isMp3);
            var end = p.End >= total ? length : Align(src, (long)(p.End.TotalSeconds * bytesPerSecond), isMp3);
            if (end <= start) continue;

            var name = $"{p.Number:00} - " + (string.IsNullOrWhiteSpace(p.Artist) ? p.Title : $"{p.Artist} - {p.Title}");
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (name.Length > 150) name = name.Substring(0, 150);
            var dest = Path.Combine(outDir, name + ext);

            using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write);
            if (isMp3) WriteId3(dst, p.Title, p.Artist, album, p.Number);
            src.Position = start;
            Copy(src, dst, end - start);
        }
        return outDir;
    }

    private static void Copy(Stream src, Stream dst, long count)
    {
        var buf = new byte[81920];
        while (count > 0)
        {
            var n = src.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            count -= n;
        }
    }

    /// <summary>Moves a byte position forward to the start of the next audio frame.</summary>
    private static long Align(FileStream src, long pos, bool mp3)
    {
        pos = Math.Clamp(pos, 0, src.Length);
        var buf = new byte[64 * 1024];
        src.Position = pos;
        var n = src.Read(buf, 0, buf.Length);
        for (var i = 0; i + 3 < n; i++)
        {
            if (buf[i] != 0xFF) continue;
            var b1 = buf[i + 1];
            if (mp3)
            {
                if ((b1 & 0xE0) != 0xE0) continue;
                var version = (b1 >> 3) & 3; var layer = (b1 >> 1) & 3;
                var b2 = buf[i + 2];
                var bitrate = b2 >> 4; var rate = (b2 >> 2) & 3;
                if (version == 1 || layer == 0 || bitrate == 0 || bitrate == 15 || rate == 3) continue;
                return pos + i;
            }
            if ((b1 & 0xF6) == 0xF0) return pos + i; // AAC ADTS sync
        }
        return pos;
    }

    // Minimal ID3v2.3 tag: title, artist, album, track number.
    private static void WriteId3(Stream s, string title, string? artist, string album, int track)
    {
        using var body = new MemoryStream();
        void Frame(string id, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var data = new List<byte> { 0x01, 0xFF, 0xFE }; // UTF-16 with BOM
            data.AddRange(Encoding.Unicode.GetBytes(text));
            var size = data.Count;
            body.Write(Encoding.ASCII.GetBytes(id));
            body.Write(new[] { (byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, (byte)0, (byte)0 });
            body.Write(data.ToArray());
        }
        Frame("TIT2", title);
        Frame("TPE1", artist);
        Frame("TALB", album);
        Frame("TRCK", track.ToString(CultureInfo.InvariantCulture));

        var len = (int)body.Length;
        s.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0,
            (byte)((len >> 21) & 0x7F), (byte)((len >> 14) & 0x7F), (byte)((len >> 7) & 0x7F), (byte)(len & 0x7F) });
        body.Position = 0;
        body.CopyTo(s);
    }
}
