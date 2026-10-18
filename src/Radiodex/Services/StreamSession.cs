using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Radiodex.Services.Sources;

namespace Radiodex.Services;

public sealed class SongMark
{
    public TimeSpan Offset { get; init; }
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public DateTime LocalTime { get; init; } = DateTime.Now;

    /// <summary>Most stations send "Artist - Title".</summary>
    public static (string Title, string? Artist) Split(string streamTitle)
    {
        var s = streamTitle.Trim();
        var i = s.IndexOf(" - ", StringComparison.Ordinal);
        if (i > 0 && i < s.Length - 3) return (s.Substring(i + 3).Trim(), s.Substring(0, i).Trim());
        return (s, null);
    }
}

/// <summary>
/// The one connection to the station. It reads the audio and the song titles (ICY metadata),
/// saves the audio while recording, and hands the audio to Windows' player through a small
/// local address (http://127.0.0.1:port/live.ext) with a content type Windows understands.
///
/// Why: many servers send "audio/aacp" (AAC+), which browsers play but Windows' player does
/// not recognise, so it would sit on "connecting" forever. One connection also means stations
/// that allow a single listener per IP still work, and a short server drop doesn't stop the
/// player: the relay reconnects underneath it.
/// </summary>
public sealed class StreamSession : IDisposable
{
    private static readonly Regex StreamTitleRx = new(@"StreamTitle\s*=\s*'(.*?)';", RegexOptions.Singleline);
    private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(15);

    private volatile string _stationName;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly TcpListener _listener;
    private readonly List<Channel<byte[]>> _clients = new();
    private readonly Timer _watchdog;

    private volatile string _url;
    private CancellationTokenSource? _conn;
    private TaskCompletionSource<bool> _ready = NewReady();
    private Recording? _recording;
    private string? _lastTitle;
    private long _lastDataTicks = DateTime.UtcNow.Ticks;
    private int _failures;
    private bool _downReported;
    private volatile bool _switchRequested;
    private volatile bool _unreadable;

    public event Action<string>? TitleChanged;
    public event Action<string>? RecordingFailed;
    /// <summary>The station stopped answering (after a few quick retries). Time to look for a new link.</summary>
    public event Action? StreamDown;

    /// <summary>Used for new recording file names; follows renames.</summary>
    public string StationName { get => _stationName; set => _stationName = value; }

    public bool SupportsMetadata { get; private set; }
    public bool IsHls { get; private set; }
    public string? ContentType { get; private set; }
    public int Port { get; }

    /// <summary>Local address for the player, once <see cref="WaitReadyAsync"/> returned true.</summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}/live.{LocalExtension}";

    /// <summary>False for HLS and for old servers only Windows can read: there is no audio passing through here to save.</summary>
    public bool CanRecord => !IsHls && !_unreadable;

    public bool IsRecording { get { lock (_lock) return _recording != null; } }

    public string? CurrentRecordingPath { get { lock (_lock) return _recording?.AudioPath; } }

    public TimeSpan RecordingElapsed { get { lock (_lock) return _recording?.Elapsed ?? TimeSpan.Zero; } }

    public StreamSession(string url, string stationName)
    {
        _url = url;
        _stationName = stationName;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _watchdog = new Timer(_ => CheckStall(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        _ = Task.Run(AcceptLoopAsync);
        _ = Task.Run(RunAsync);
    }

    private static TaskCompletionSource<bool> NewReady() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// True when the relay is receiving audio and the player should use <see cref="LocalUrl"/>.
    /// False (HLS, an old server HttpClient can't read, or no answer in time): play the station URL directly.
    /// </summary>
    public async Task<bool> WaitReadyAsync(TimeSpan timeout)
    {
        var t = _ready.Task;
        var done = await Task.WhenAny(t, Task.Delay(timeout)).ConfigureAwait(false);
        return done == t && t.Result;
    }

    /// <summary>Moves to a new stream link (after auto-repair). A running recording continues in the same file.</summary>
    public void SwitchUrl(string url)
    {
        _url = url;
        _ready = NewReady();
        _failures = 0;
        _downReported = false;
        IsHls = false;
        _unreadable = false;
        _switchRequested = true;
        try { _conn?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ---------------- upstream ----------------

    private async Task RunAsync()
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_cts.IsCancellationRequested)
        {
            if (IsHls || _unreadable)
            {
                // Windows plays these itself (HLS, or old "ICY 200" servers HttpClient can't read);
                // nothing for us to do until the link changes.
                try { await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var conn = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _conn = conn;
            _switchRequested = false;
            var switched = false;
            try
            {
                await ReadOnceAsync(conn.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                // SwitchUrl (reconnect at once) or a stall (counts as a failure).
                switched = _switchRequested;
            }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse)
            {
                _unreadable = true;
                _ready.TrySetResult(false);
                continue;
            }
            catch (Exception)
            {
                // Network error, server closed, bad link: retry below.
            }
            finally
            {
                conn.Dispose();
            }

            if (switched)
            {
                delay = TimeSpan.FromSeconds(1);
                continue;
            }

            _failures++;
            _ready.TrySetResult(false);
            if (_failures >= 3 && !_downReported)
            {
                _downReported = true;
                StreamDown?.Invoke();
            }
            try { await Task.Delay(delay, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(20, delay.TotalSeconds * 2));
        }
    }

    private async Task ReadOnceAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _url);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        using var resp = await Http.Streaming.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        ContentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        if (ContentType != null && (ContentType.Contains("mpegurl") || ContentType.Contains("x-mpegurl")))
        {
            IsHls = true;
            _ready.TrySetResult(false);
            return;
        }

        var metaInt = 0;
        if (resp.Headers.TryGetValues("icy-metaint", out var vals)) int.TryParse(vals.FirstOrDefault(), out metaInt);
        SupportsMetadata = metaInt > 0;

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var audio = new byte[16 * 1024];
        Touch();

        if (metaInt <= 0)
        {
            while (true)
            {
                var n = await stream.ReadAsync(audio.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return;
                OnAudio(audio, n);
            }
        }

        var remaining = metaInt;
        var one = new byte[1];
        while (true)
        {
            while (remaining > 0)
            {
                var n = await stream.ReadAsync(audio.AsMemory(0, Math.Min(audio.Length, remaining)), ct).ConfigureAwait(false);
                if (n <= 0) return;
                OnAudio(audio, n);
                remaining -= n;
            }
            remaining = metaInt;

            if (await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false) <= 0) return;
            var len = one[0] * 16;
            if (len == 0) continue;

            var meta = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = await stream.ReadAsync(meta.AsMemory(read, len - read), ct).ConfigureAwait(false);
                if (n <= 0) return;
                read += n;
            }
            OnMetadata(Decode(meta));
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastDataTicks, DateTime.UtcNow.Ticks);

    private void CheckStall()
    {
        if (IsHls || _cts.IsCancellationRequested) return;
        var last = new DateTime(Interlocked.Read(ref _lastDataTicks), DateTimeKind.Utc);
        if (DateTime.UtcNow - last > StallLimit)
        {
            Touch();
            try { _conn?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void OnAudio(byte[] buf, int count)
    {
        Touch();
        if (_failures != 0) { _failures = 0; _downReported = false; }
        _ready.TrySetResult(true);

        var chunk = new byte[count];
        Buffer.BlockCopy(buf, 0, chunk, 0, count);

        lock (_lock)
        {
            foreach (var c in _clients) c.Writer.TryWrite(chunk);

            if (_recording != null)
            {
                try { _recording.Write(chunk); }
                catch (Exception ex)
                {
                    var r = _recording;
                    _recording = null;
                    try { r.Close(); } catch { }
                    RecordingFailed?.Invoke(ex.Message);
                }
            }
        }
    }

    private static string Decode(byte[] bytes)
    {
        var end = bytes.Length;
        while (end > 0 && bytes[end - 1] == 0) end--;
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, end); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes, 0, end); }
    }

    private void OnMetadata(string meta)
    {
        var m = StreamTitleRx.Match(meta);
        if (!m.Success) return;
        var title = m.Groups[1].Value.Trim();
        if (title.Length == 0 || title == _lastTitle) return;
        _lastTitle = title;
        lock (_lock) _recording?.Mark(title);
        TitleChanged?.Invoke(title);
    }

    // ---------------- local relay for the player ----------------

    private string LocalExtension => StreamProbe.CodecFrom(ContentType) switch
    {
        "aac" => "aac",
        "ogg" or "opus" => "ogg",
        "flac" => "flac",
        _ => "mp3"
    };

    private string LocalContentType => LocalExtension switch
    {
        "aac" => "audio/aac",   // "audio/aacp" and friends become plain AAC (ADTS) for Windows
        "ogg" => "audio/ogg",
        "flac" => "audio/flac",
        _ => "audio/mpeg"
    };

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        try
        {
            using (client)
            {
                client.NoDelay = true;
                var net = client.GetStream();

                // Read the request; we only serve one thing, so just note whether it is a HEAD.
                var isHead = false;
                using (var hdrCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    hdrCts.CancelAfter(TimeSpan.FromSeconds(5));
                    var buf = new byte[4096];
                    var total = 0;
                    while (total < buf.Length)
                    {
                        var n = await net.ReadAsync(buf.AsMemory(total), hdrCts.Token).ConfigureAwait(false);
                        if (n <= 0) return;
                        total += n;
                        var text = Encoding.ASCII.GetString(buf, 0, total);
                        if (text.Contains("\r\n\r\n"))
                        {
                            isHead = text.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase);
                            break;
                        }
                    }
                }

                var header = "HTTP/1.1 200 OK\r\n" +
                             $"Content-Type: {LocalContentType}\r\n" +
                             "Cache-Control: no-cache, no-store\r\n" +
                             "Accept-Ranges: none\r\n" +
                             "Connection: close\r\n\r\n";
                var hb = Encoding.ASCII.GetBytes(header);
                await net.WriteAsync(hb, _cts.Token).ConfigureAwait(false);
                if (isHead) return;

                lock (_lock) _clients.Add(channel);
                await foreach (var chunk in channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                    await net.WriteAsync(chunk, _cts.Token).ConfigureAwait(false);
            }
        }
        catch
        {
            // Player disconnected or session closed.
        }
        finally
        {
            lock (_lock) _clients.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    // ---------------- recording ----------------

    public string StartRecording(string folder)
    {
        lock (_lock)
        {
            if (_recording != null) return _recording.AudioPath;
            Directory.CreateDirectory(folder);
            var safe = string.Concat(_stationName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
            if (safe.Length == 0) safe = "Radiodex";
            var path = Path.Combine(folder, $"{safe}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{LocalExtension}");
            _recording = new Recording(path, _stationName);
            if (_lastTitle != null) _recording.Mark(_lastTitle);
            return path;
        }
    }

    /// <summary>Stops recording and writes the .cue song list. Returns the audio file path.</summary>
    public string? StopRecording()
    {
        Recording? r;
        lock (_lock)
        {
            r = _recording;
            _recording = null;
        }
        if (r == null) return null;
        r.Close();
        return r.AudioPath;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watchdog.Dispose();
        try { _listener.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var c in _clients) c.Writer.TryComplete();
            _clients.Clear();
        }
        StopRecording();
    }

    private sealed class Recording
    {
        private readonly FileStream _file;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<SongMark> _marks = new();
        private readonly string _station;

        public string AudioPath { get; }
        public TimeSpan Elapsed => _clock.Elapsed;

        public Recording(string path, string station)
        {
            AudioPath = path;
            _station = station;
            _file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        }

        public void Write(byte[] chunk) => _file.Write(chunk, 0, chunk.Length);

        public void Mark(string streamTitle)
        {
            var (title, artist) = SongMark.Split(streamTitle);
            _marks.Add(new SongMark { Offset = _clock.Elapsed, Title = title, Artist = artist });
        }

        public void Close()
        {
            _clock.Stop();
            _file.Flush();
            _file.Dispose();
            if (_marks.Count > 0) WriteCue();
        }

        // Standard CUE sheet: players such as foobar2000 and VLC show it as a track list.
        private void WriteCue()
        {
            static string Q(string s) => s.Replace("\"", "'");
            var name = Path.GetFileName(AudioPath);
            var kind = name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "MP3" : "WAVE";
            var sb = new StringBuilder();
            sb.AppendLine($"PERFORMER \"{Q(_station)}\"");
            sb.AppendLine($"TITLE \"{Q(_station)} {DateTime.Now:yyyy-MM-dd}\"");
            sb.AppendLine($"FILE \"{Q(name)}\" {kind}");
            for (var i = 0; i < _marks.Count; i++)
            {
                var m = _marks[i];
                var frames = (long)Math.Round(m.Offset.TotalSeconds * 75);
                var mm = frames / (75 * 60);
                var ss = frames / 75 % 60;
                var ff = frames % 75;
                sb.AppendLine($"  TRACK {i + 1:00} AUDIO");
                sb.AppendLine($"    TITLE \"{Q(m.Title)}\"");
                if (!string.IsNullOrEmpty(m.Artist)) sb.AppendLine($"    PERFORMER \"{Q(m.Artist!)}\"");
                sb.AppendLine($"    INDEX 01 {mm:00}:{ss:00}:{ff:00}");
            }
            File.WriteAllText(Path.ChangeExtension(AudioPath, ".cue"), sb.ToString(), new UTF8Encoding(true));
        }
    }
}
