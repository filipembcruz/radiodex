using System;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Radiodex.Services;

/// <summary>Plays a saved recording, with seeking. Events come on a background thread.</summary>
public sealed class FilePlayer : IDisposable
{
    private readonly MediaPlayer _player = new();
    private MediaSource? _source; // disposed on Close so the file handle is released (delete to Recycle Bin)
    private int _gen;             // a later Open/Close wins over an Open still awaiting its StorageFile

    public event Action<TimeSpan>? Opened;
    public event Action<bool>? PlayingChanged;
    public event Action<string>? Failed;

    public FilePlayer()
    {
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.AutoPlay = false;
        _player.CommandManager.IsEnabled = false; // the radio keeps the media keys
        _player.MediaOpened += (s, _) => Opened?.Invoke(s.PlaybackSession.NaturalDuration);
        _player.MediaFailed += (_, e) => Failed?.Invoke(string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage);
        _player.PlaybackSession.PlaybackStateChanged += (s, _) =>
            PlayingChanged?.Invoke(s.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening);
    }

    public async void Open(string path)
    {
        var gen = ++_gen;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (gen != _gen) return;
            var old = _source;
            _source = MediaSource.CreateFromStorageFile(file);
            _player.Source = _source;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    public TimeSpan Position
    {
        get => _player.PlaybackSession.Position;
        set => _player.PlaybackSession.Position = value < TimeSpan.Zero ? TimeSpan.Zero : value;
    }

    public TimeSpan Duration => _player.PlaybackSession.NaturalDuration;

    public bool IsPlaying => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public double Volume { get => _player.Volume; set => _player.Volume = Math.Clamp(value, 0, 1); }

    public void Play() => _player.Play();
    public void Pause() => _player.Pause();

    public void Close()
    {
        _gen++;
        _player.Pause();
        _player.Source = null;
        _source?.Dispose();
        _source = null;
    }

    public void Dispose()
    {
        _gen++;
        _player.Source = null;
        _source?.Dispose();
        _source = null;
        _player.Dispose();
    }
}
