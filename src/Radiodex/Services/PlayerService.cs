using System;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Radiodex.Services;

public enum PlayState { Stopped, Connecting, Playing, Paused }

/// <summary>
/// Plays streams with Windows' own media player. Because it is the system player,
/// the keyboard media keys and the Windows volume flyout (with the station logo and
/// song) work automatically.
/// Events are raised on a background thread; the UI marshals them.
/// </summary>
public sealed class PlayerService : IDisposable
{
    private readonly MediaPlayer _player = new();
    private MediaPlaybackItem? _item;
    private string? _url;
    private string _stationName = "";
    private string? _logo;
    private RandomAccessStreamReference? _thumb;
    private string? _songTitle;
    private string? _songArtist;
    private DateTime? _pausedAt;

    public event Action<PlayState>? StateChanged;
    public event Action<string>? Failed;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    public PlayState State { get; private set; } = PlayState.Stopped;

    public PlayerService()
    {
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.AutoPlay = false;
        _player.MediaFailed += (_, e) => Failed?.Invoke(string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage);
        _player.MediaEnded += (_, _) => Failed?.Invoke("stream ended");
        _player.PlaybackSession.PlaybackStateChanged += (s, _) => OnSessionState(s.PlaybackState);

        // Media keys "next/previous" switch stations.
        var cm = _player.CommandManager;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; NextRequested?.Invoke(); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; PreviousRequested?.Invoke(); };
    }

    private void OnSessionState(MediaPlaybackState s)
    {
        var state = s switch
        {
            MediaPlaybackState.Playing => PlayState.Playing,
            MediaPlaybackState.Paused => PlayState.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => PlayState.Connecting,
            _ => PlayState.Stopped
        };
        if (state == PlayState.Paused && _url == null) state = PlayState.Stopped;
        State = state;
        StateChanged?.Invoke(state);
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public void Play(string url, string stationName, string? logoUrl)
    {
        var sameLogo = _logo == logoUrl && _thumb != null;
        _url = url;
        _stationName = stationName;
        _logo = logoUrl;
        _pausedAt = null;
        _songTitle = _songArtist = null;

        _item = new MediaPlaybackItem(MediaSource.CreateFromUri(new Uri(url)));
        if (sameLogo) ApplyDisplay(null, null);
        else LoadThumb(logoUrl);
        _player.Source = _item;
        State = PlayState.Connecting;
        StateChanged?.Invoke(State);
        _player.Play();
    }

    /// <summary>Resumes. After a long pause it reconnects so you hear the live broadcast, not the buffer.</summary>
    public void Resume()
    {
        if (_url == null) return;
        if (_pausedAt is DateTime t && DateTime.UtcNow - t > TimeSpan.FromSeconds(20))
        {
            Play(_url, _stationName, _logo);
            return;
        }
        _pausedAt = null;
        _player.Play();
    }

    public void Pause()
    {
        _pausedAt = DateTime.UtcNow;
        _player.Pause();
    }

    public void Stop()
    {
        _url = null;
        _player.Pause();
        _player.Source = null;
        _item = null;
        State = PlayState.Stopped;
        StateChanged?.Invoke(State);
    }

    /// <summary>Shows the current song in the Windows media flyout.</summary>
    public void SetSong(string? title, string? artist) => ApplyDisplay(title, artist);

    /// <summary>After the user renames a station or changes its cover.</summary>
    public void UpdateStation(string stationName, string? logoUrl)
    {
        _stationName = stationName;
        if (_logo != logoUrl)
        {
            _logo = logoUrl;
            LoadThumb(logoUrl);
        }
        else
        {
            ApplyDisplay(_songTitle, _songArtist);
        }
    }

    private async void LoadThumb(string? logo)
    {
        _thumb = null;
        try
        {
            if (logo != null && Uri.TryCreate(logo, UriKind.Absolute, out var uri))
            {
                if (uri.IsFile)
                {
                    // Custom covers are local files; the flyout needs them as a StorageFile.
                    var file = await StorageFile.GetFileFromPathAsync(uri.LocalPath);
                    if (_logo == logo) _thumb = RandomAccessStreamReference.CreateFromFile(file);
                }
                else
                {
                    _thumb = RandomAccessStreamReference.CreateFromUri(uri);
                }
            }
        }
        catch
        {
            _thumb = null;
        }
        ApplyDisplay(_songTitle, _songArtist);
    }

    private void ApplyDisplay(string? title, string? artist)
    {
        _songTitle = title;
        _songArtist = artist;
        var item = _item;
        if (item == null) return;
        try
        {
            var props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = string.IsNullOrWhiteSpace(title) ? _stationName : title;
            props.MusicProperties.Artist = string.IsNullOrWhiteSpace(title) ? "Radiodex" : (artist ?? _stationName);
            props.MusicProperties.AlbumTitle = _stationName;
            if (_thumb != null) props.Thumbnail = _thumb;
            item.ApplyDisplayProperties(props);
        }
        catch
        {
            // Display info is cosmetic.
        }
    }

    public void Dispose()
    {
        _player.Source = null;
        _player.Dispose();
    }
}
