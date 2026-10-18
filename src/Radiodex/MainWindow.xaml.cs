using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Radiodex.Models;
using Radiodex.Services;
using Radiodex.Services.Sources;

namespace Radiodex;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Station> _stations;
    private readonly ICollectionView _view;
    private readonly ObservableCollection<SongMark> _history = new();
    private readonly AppSettings _settings;
    private readonly List<LikedSong> _liked;
    private readonly PlayerService _player = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };

    private Station? _current;
    private StreamSession? _session;
    private bool _preparing;
    private string? _lastRecordingPath;
    private bool _favOnly;
    private bool _wantPlaying;
    private string? _songTitle;
    private string? _songArtist;

    // Auto-repair state
    private bool _healing;
    private bool _pendingHeal;
    private int _failCount;
    private DateTime _nextHealAllowed = DateTime.MinValue;
    private DateTime? _connectingSince;
    private DateTime? _playingSince;
    private string? _linkStatusKey;
    private CancellationTokenSource? _healCts;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);

        _settings = Storage.LoadSettings();
        if (!string.IsNullOrEmpty(_settings.Language)) Loc.I.Language = _settings.Language;

        _stations = new ObservableCollection<Station>(Storage.LoadStations());
        _liked = Storage.LoadLikedSongs();
        foreach (var s in _stations) s.Status = CheckedText(s);

        _view = CollectionViewSource.GetDefaultView(_stations);
        _view.Filter = o => !_favOnly || (o is Station st && st.Favorite);
        StationList.ItemsSource = _view;
        HistoryList.ItemsSource = _history;

        VolumeSlider.Value = Math.Clamp(_settings.Volume, 0, 100);
        _player.Volume = VolumeSlider.Value / 100.0;

        _player.StateChanged += st => Dispatcher.InvokeAsync(() => OnPlayState(st));
        _player.Failed += _ => Dispatcher.InvokeAsync(OnPlayerFailed);
        _player.NextRequested += () => Dispatcher.InvokeAsync(() => Step(+1));
        _player.PreviousRequested += () => Dispatcher.InvokeAsync(() => Step(-1));

        Loc.I.PropertyChanged += (_, _) => RefreshTexts();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        Closing += OnClosing;
        RefreshTexts();
        UpdateEmptyState();

        // Pick up where the user left off.
        var last = _stations.FirstOrDefault(s => s.Id == _settings.LastStationId);
        if (last != null) StationList.SelectedItem = last;
    }

    // ================= Tuning =================

    private void OnStationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StationList.SelectedItem is Station s && !ReferenceEquals(s, _current)) Tune(s);
    }

    private void Tune(Station s)
    {
        StopRecordingIfAny();
        _session?.Dispose();
        _session = null;
        _healCts?.Cancel();

        _current = s;
        _history.Clear();
        _songTitle = _songArtist = null;
        _failCount = 0;
        _pendingHeal = false;
        _nextHealAllowed = DateTime.MinValue;
        _linkStatusKey = null;

        Welcome.Visibility = Visibility.Collapsed;
        NowPanel.Visibility = Visibility.Visible;
        StationName.Text = s.Name;
        StationPlace.Text = s.Place ?? "";
        BigInitials.Text = s.Initials;
        LoadLogo(s.LogoUrl);
        UpdateStationInfo();
        UpdateSong();

        _settings.LastStationId = s.Id;
        Storage.SaveSettings(_settings);

        StartPlayback();
        if (s.Source == StationSource.RadioBrowser && s.SourceRef != null) RadioBrowser.CountClick(s.SourceRef);
    }

    /// <summary>
    /// Opens (or re-points) the station connection, then plays it through the local relay.
    /// If the relay can't read this stream (HLS, very old servers), Windows plays the link directly.
    /// </summary>
    private async void StartPlayback()
    {
        if (_current == null) return;
        var s = _current;
        _wantPlaying = true;
        _preparing = true;
        _connectingSince ??= DateTime.UtcNow;
        UpdatePlayButton();

        if (_session == null)
        {
            _session = new StreamSession(s.StreamUrl, s.Name);
            var m = _session;
            m.TitleChanged += t => Dispatcher.InvokeAsync(() => { if (ReferenceEquals(m, _session)) OnSong(t); });
            m.RecordingFailed += msg => Dispatcher.InvokeAsync(() =>
            {
                LastRecording.Text = Loc.F("RecordFailed", msg);
                UpdateRecButton();
            });
            m.StreamDown += () => Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(m, _session) && _wantPlaying) _pendingHeal = true;
            });
        }
        else
        {
            _session.SwitchUrl(s.StreamUrl);
        }

        var session = _session;
        var relay = await session.WaitReadyAsync(TimeSpan.FromSeconds(12));
        _preparing = false;
        if (!ReferenceEquals(session, _session) || !ReferenceEquals(s, _current) || !_wantPlaying)
        {
            UpdatePlayButton();
            return;
        }

        try
        {
            _player.Play(relay ? session.LocalUrl : s.StreamUrl, s.Name, s.LogoUrl);
        }
        catch (Exception)
        {
            _pendingHeal = true;
        }
        UpdatePlayButton();
    }

    private void OnPlayState(PlayState st)
    {
        _connectingSince = st == PlayState.Connecting ? _connectingSince ?? DateTime.UtcNow : null;
        // Play/pause can also come from the keyboard media keys or the Windows flyout.
        if (st == PlayState.Paused) _wantPlaying = false;
        if (st == PlayState.Playing)
        {
            _wantPlaying = true;
            _playingSince ??= DateTime.UtcNow;
            if (_current != null)
            {
                _current.LinkCheckedUtc = DateTime.UtcNow;
                if (_linkStatusKey is null or "StatusSearching") SetLinkStatus("StatusOnline");
                _current.Status = Loc.T("StatusOnline");
            }
        }
        else
        {
            _playingSince = null;
        }
        UpdatePlayButton();
    }

    private void OnPlayerFailed()
    {
        if (!_wantPlaying || _current == null) return;
        _pendingHeal = true;
    }

    // ================= Auto-repair =================

    private void OnTick()
    {
        if (_session?.IsRecording == true) UpdateRecButton();

        // A stream that has played fine for a minute resets the back-off.
        if (_playingSince is DateTime p && DateTime.UtcNow - p > TimeSpan.FromSeconds(60)) _failCount = 0;

        // Stuck connecting: treat as a failure.
        if (_wantPlaying && _connectingSince is DateTime c && DateTime.UtcNow - c > TimeSpan.FromSeconds(25))
        {
            _connectingSince = DateTime.UtcNow;
            _pendingHeal = true;
        }

        if (_pendingHeal && !_healing && DateTime.UtcNow >= _nextHealAllowed)
        {
            _pendingHeal = false;
            _ = HealAsync(manual: false);
        }
    }

    private async Task HealAsync(bool manual)
    {
        var station = _current;
        if (station == null || _healing) return;
        _healing = true;
        _healCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = _healCts.Token;
        SetLinkStatus("StatusSearching");
        station.Status = Loc.T("StatusSearching");
        UpdateRefreshButton();

        try
        {
            var r = await StationResolver.RefreshAsync(station, ct);
            if (!ReferenceEquals(station, _current)) return;

            if (r.Found && r.Changed && r.StreamUrl != null)
            {
                station.StreamUrl = r.StreamUrl;
                if (!string.IsNullOrEmpty(r.Codec)) station.Codec = r.Codec;
                if (r.Bitrate > 0) station.Bitrate = r.Bitrate;
                station.LinkCheckedUtc = DateTime.UtcNow;
                Storage.SaveStations(_stations);
                SetLinkStatus("StatusUpdated");
                station.Status = Loc.T("StatusUpdated");
                UpdateStationInfo();
                if (_wantPlaying || manual) StartPlayback();
            }
            else if (r.Found)
            {
                station.LinkCheckedUtc = DateTime.UtcNow;
                Storage.SaveStations(_stations);
                SetLinkStatus(manual ? "StatusSame" : "StatusOnline");
                station.Status = Loc.T("StatusOnline");
                if (_wantPlaying && _player.State != PlayState.Playing) StartPlayback();
            }
            else
            {
                SetLinkStatus("StatusFailed");
                station.Status = Loc.T("StatusOffline");
                _failCount = Math.Max(_failCount, 4);
                if (_wantPlaying) _pendingHeal = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(station, _current)) SetLinkStatus("StatusFailed");
        }
        finally
        {
            _healing = false;
            _failCount++;
            // Back-off between automatic attempts: 10 s, 30 s, 2 min, then every 5 min.
            var wait = _failCount switch { <= 1 => 10, 2 => 30, 3 => 120, _ => 300 };
            _nextHealAllowed = DateTime.UtcNow.AddSeconds(wait);
            UpdateRefreshButton();
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_current == null || _healing) return;
        _ = HealAsync(manual: true);
    }

    // ================= Controls =================

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (_preparing)
        {
            _wantPlaying = false;
            _preparing = false;
            UpdatePlayButton();
            return;
        }
        if (_player.State is PlayState.Playing or PlayState.Connecting)
        {
            _wantPlaying = false;
            _pendingHeal = false;
            _player.Pause();
        }
        else
        {
            _wantPlaying = true;
            if (_player.State == PlayState.Paused) _player.Resume();
            else StartPlayback();
        }
        UpdatePlayButton();
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)Math.Round(e.NewValue);
        if (VolumeText != null) VolumeText.Text = v + "%";
        _player.Volume = v / 100.0;
        if (_settings != null && _settings.Volume != v)
        {
            _settings.Volume = v;
            Storage.SaveSettings(_settings);
        }
    }

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_session == null || _current == null) return;
        if (_session.IsRecording)
        {
            StopRecordingIfAny();
        }
        else
        {
            if (!_session.CanRecord || _current.StreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                LastRecording.Text = Loc.T("RecordNotSupported");
                return;
            }
            try
            {
                var path = _session.StartRecording(_settings.RecordingsFolder);
                LastRecording.Text = Path.GetFileName(path);
            }
            catch (Exception ex)
            {
                LastRecording.Text = Loc.F("RecordFailed", ex.Message);
            }
        }
        UpdateRecButton();
    }

    private void StopRecordingIfAny()
    {
        var path = _session?.StopRecording();
        if (path != null)
        {
            _lastRecordingPath = path;
            LastRecording.Text = Loc.F("RecordSaved", Path.GetFileName(path));
        }
        UpdateRecButton();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.RecordingsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _settings.RecordingsFolder + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnFavorite(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _current.Favorite = !_current.Favorite;
        Storage.SaveStations(_stations);
        _view.Refresh();
        UpdateFavButton();
        RefreshTabs();
    }

    private void OnLikeSong(object sender, RoutedEventArgs e)
    {
        if (_current == null || string.IsNullOrEmpty(_songTitle)) return;
        var existing = FindLiked();
        if (existing != null) _liked.Remove(existing);
        else _liked.Add(new LikedSong { Title = _songTitle!, Artist = _songArtist, StationName = _current.Name });
        Storage.SaveLikedSongs(_liked);
        UpdateSong();
    }

    private LikedSong? FindLiked() =>
        _liked.FirstOrDefault(l => string.Equals(l.Title, _songTitle, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(l.Artist ?? "", _songArtist ?? "", StringComparison.OrdinalIgnoreCase));

    private void OnSong(string raw)
    {
        var (title, artist) = SongMark.Split(raw);
        _songTitle = title;
        _songArtist = artist;
        _history.Insert(0, new SongMark { Title = title, Artist = artist, LocalTime = DateTime.Now });
        while (_history.Count > 12) _history.RemoveAt(_history.Count - 1);
        _player.SetSong(title, artist);
        UpdateSong();
    }

    /// <summary>Media keys next/previous: move through the visible list.</summary>
    private void Step(int dir)
    {
        var list = _view.Cast<Station>().ToList();
        if (list.Count == 0) return;
        var i = _current == null ? -1 : list.IndexOf(_current);
        var next = list[((i + dir) % list.Count + list.Count) % list.Count];
        StationList.SelectedItem = next;
    }

    // ================= Sidebar =================

    private void OnAddKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnAddClick(sender, e);
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dlg = new AddStationWindow(AddCandidate, IsAdded, AddBox.Text.Trim()) { Owner = this };
        dlg.ShowDialog();
        AddBox.Text = "";
        UpdateEmptyState();
        RefreshTabs();
        if (dlg.PlayNow != null)
        {
            if (_favOnly && !dlg.PlayNow.Favorite) OnTabAll(this, new RoutedEventArgs());
            StationList.SelectedItem = dlg.PlayNow;
            StationList.ScrollIntoView(dlg.PlayNow);
        }
    }

    private bool IsAdded(StationCandidate c) =>
        _stations.Any(s => (c.SourceRef != null && s.SourceRef == c.SourceRef) ||
                           string.Equals(s.StreamUrl, c.StreamUrl, StringComparison.OrdinalIgnoreCase));

    private Station AddCandidate(StationCandidate c, bool favorite)
    {
        var existing = _stations.FirstOrDefault(s => (c.SourceRef != null && s.SourceRef == c.SourceRef) ||
                                                     string.Equals(s.StreamUrl, c.StreamUrl, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var st = c.ToStation();
        st.Favorite = favorite;
        st.Status = CheckedText(st);
        _stations.Add(st);
        Storage.SaveStations(_stations);
        UpdateEmptyState();
        return st;
    }

    private void OnEditStation(object sender, RoutedEventArgs e)
    {
        var s = (sender is MenuItem ? StationList.SelectedItem as Station : null) ?? _current;
        if (s == null) return;
        var dlg = new EditStationWindow(s) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        Storage.SaveStations(_stations);
        if (ReferenceEquals(s, _current))
        {
            StationName.Text = s.Name;
            BigInitials.Text = s.Initials;
            LoadLogo(s.LogoUrl);
            _player.UpdateStation(s.Name, s.LogoUrl);
            // New recordings use the new name.
            if (_session != null) _session.StationName = s.Name;
        }
        // The list shows the cover through a binding; nudge it to reload the picture.
        var logo = s.LogoUrl;
        s.LogoUrl = null;
        s.LogoUrl = logo;
    }

    private void OnViewRecordings(object sender, RoutedEventArgs e)
    {
        var dlg = new RecordingsWindow(_settings.RecordingsFolder, _liked, () => Storage.SaveLikedSongs(_liked),
            beforePlay: () =>
            {
                if (_player.State is PlayState.Playing or PlayState.Connecting)
                {
                    _wantPlaying = false;
                    _pendingHeal = false;
                    _player.Pause();
                }
            },
            select: _lastRecordingPath, recordingNow: _session?.IsRecording == true ? _session.CurrentRecordingPath : null) { Owner = this };
        dlg.ShowDialog();
        UpdateSong();
    }

    private void OnLikedSongs(object sender, RoutedEventArgs e)
    {
        var dlg = new LikedSongsWindow(_liked, () => Storage.SaveLikedSongs(_liked)) { Owner = this };
        dlg.ShowDialog();
        UpdateSong();
    }

    private void OnRemoveStation(object sender, RoutedEventArgs e) => RemoveSelected();

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelected();
    }

    private void RemoveSelected()
    {
        if (StationList.SelectedItem is not Station s) return;
        var ok = MessageBox.Show(this, Loc.F("RemoveConfirm", s.Name), "Radiodex", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        if (ReferenceEquals(s, _current))
        {
            StopRecordingIfAny();
            _session?.Dispose();
            _session = null;
            _wantPlaying = false;
            _player.Stop();
            _current = null;
            NowPanel.Visibility = Visibility.Collapsed;
            Welcome.Visibility = Visibility.Visible;
        }
        _stations.Remove(s);
        Storage.SaveStations(_stations);
        UpdateEmptyState();
        RefreshTabs();
    }

    private void OnTabAll(object sender, RoutedEventArgs e)
    {
        _favOnly = false;
        _view.Refresh();
        RefreshTabs();
    }

    private void OnTabFav(object sender, RoutedEventArgs e)
    {
        _favOnly = true;
        _view.Refresh();
        RefreshTabs();
    }

    private void OnLangPt(object sender, RoutedEventArgs e) => SetLanguage("pt");
    private void OnLangEn(object sender, RoutedEventArgs e) => SetLanguage("en");

    private void SetLanguage(string lang)
    {
        Loc.I.Language = lang;
        _settings.Language = lang;
        Storage.SaveSettings(_settings);
    }

    // ================= UI refresh =================

    private Brush B(string key) => (Brush)FindResource(key);

    private void RefreshTexts()
    {
        RefreshTabs();
        LangPt.Foreground = B(Loc.I.Language == "pt" ? "Text" : "Muted");
        LangEn.Foreground = B(Loc.I.Language == "en" ? "Text" : "Muted");
        LangPt.Background = Loc.I.Language == "pt" ? B("Selected") : Brushes.Transparent;
        LangEn.Background = Loc.I.Language == "en" ? B("Selected") : Brushes.Transparent;
        foreach (var s in _stations)
            if (!ReferenceEquals(s, _current)) s.Status = CheckedText(s);
        if (_linkStatusKey != null) SetLinkStatus(_linkStatusKey);
        else if (_current != null) LinkStatus.Text = CheckedText(_current);
        UpdatePlayButton();
        UpdateRecButton();
        UpdateFavButton();
        UpdateRefreshButton();
        UpdateSong();
        UpdateStationInfo();
    }

    private void RefreshTabs()
    {
        var favCount = _stations.Count(s => s.Favorite);
        TabAll.Content = $"{Loc.T("All")} ({_stations.Count})";
        TabFav.Content = $"{Loc.T("Favorites")} ({favCount})";
        TabAll.Background = _favOnly ? Brushes.Transparent : B("Selected");
        TabFav.Background = _favOnly ? B("Selected") : Brushes.Transparent;
        TabAll.Foreground = B(_favOnly ? "Muted" : "Text");
        TabFav.Foreground = B(_favOnly ? "Text" : "Muted");
    }

    private void UpdateEmptyState()
    {
        EmptyList.Visibility = _stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlayButton()
    {
        var st = _preparing ? PlayState.Connecting : _player.State;
        var active = st is PlayState.Playing or PlayState.Connecting;
        PlayIcon.Data = (Geometry)FindResource(active ? "IcoPause" : "IcoPlay");
        PlayButton.ToolTip = Loc.T(active ? "Pause" : "Play");
        AutomationSetName(PlayButton, Loc.T(active ? "Pause" : "Play"));

        var (text, fg, bg) = st switch
        {
            PlayState.Playing => (Loc.T("Live"), "Live", "LiveSoft"),
            PlayState.Connecting => (Loc.T("Connecting"), "Accent", "Selected"),
            _ => (Loc.T("Paused"), "Muted", "Selected")
        };
        LiveText.Text = text;
        LiveText.Foreground = B(fg);
        LiveDot.Fill = B(fg);
        LiveBadge.Background = B(bg);
    }

    private void UpdateRecButton()
    {
        var rec = _session?.IsRecording == true;
        if (rec)
        {
            var t = _session!.RecordingElapsed;
            RecText.Text = $"{Loc.T("Recording")}  {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
            RecButton.Background = B("RecSoft");
            RecButton.BorderBrush = B("Rec");
            RecDot.CornerRadius = new CornerRadius(3);
        }
        else
        {
            RecText.Text = Loc.T("Record");
            RecButton.Background = Brushes.Transparent;
            RecButton.BorderBrush = B("Border");
            RecDot.CornerRadius = new CornerRadius(6);
        }
    }

    private void UpdateFavButton()
    {
        var fav = _current?.Favorite == true;
        FavIcon.Fill = fav ? B("Accent") : Brushes.Transparent;
        FavIcon.Stroke = fav ? B("Accent") : B("Text2");
        var label = Loc.T(fav ? "Unfavorite" : "Favorite");
        FavButton.ToolTip = label;
        AutomationSetName(FavButton, label);
    }

    private void UpdateRefreshButton()
    {
        RefreshText.Text = Loc.T(_healing ? "Refreshing" : "RefreshLink");
        RefreshButton.IsEnabled = !_healing;
    }

    private void UpdateSong()
    {
        var has = !string.IsNullOrEmpty(_songTitle);
        SongTitle.Text = has ? _songTitle : "—";
        SongArtist.Text = has ? _songArtist ?? "" : "";
        LikeButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        NoHistory.Visibility = _current != null && _history.Count == 0 && _session != null && !_session.SupportsMetadata && _playingSince != null
            ? Visibility.Visible : Visibility.Collapsed;

        LikedCountText.Text = _liked.Count.ToString();
        var liked = has && FindLiked() != null;
        LikeText.Text = Loc.T(liked ? "SongLiked" : "LikeSong");
        LikeIcon.Fill = liked ? B("Heart") : Brushes.Transparent;
        LikeIcon.Stroke = liked ? B("Heart") : B("Text2");
        LikeButton.BorderBrush = liked ? B("Heart") : B("Border");
        LikeButton.Background = liked ? B("HeartSoft") : Brushes.Transparent;
    }

    private void UpdateStationInfo()
    {
        if (_current == null) return;
        FormatText.Text = _current.FormatText;
        StreamUrl.Text = _current.StreamUrl;
        var src = StationResolver.SourceName(_current.Source);
        SourceText.Text = $"{Loc.T("SourceLabel")}: {src}" + (_current.Source is StationSource.RadiosComBr or StationSource.RadioGarden && _current.SourceRef != null ? " · " + _current.SourceRef : "");
        if (_linkStatusKey == null) LinkStatus.Text = CheckedText(_current);
    }

    private void SetLinkStatus(string key)
    {
        _linkStatusKey = key;
        LinkStatus.Text = Loc.T(key);
        LinkDot.Fill = B(key switch
        {
            "StatusSearching" => "Accent",
            "StatusUpdated" => "Info",
            "StatusFailed" => "Rec",
            _ => "Live"
        });
    }

    private static string CheckedText(Station s)
    {
        if (s.LinkCheckedUtc is not DateTime t) return Loc.F("StatusChecked", Loc.T("NeverChecked"));
        var days = (DateTime.UtcNow.Date - t.ToUniversalTime().Date).Days;
        var when = days switch
        {
            <= 0 => Loc.T("Today"),
            1 => Loc.T("Yesterday"),
            _ => Loc.F("DaysAgo", days)
        };
        return Loc.F("StatusChecked", when);
    }

    private void LoadLogo(string? url)
    {
        BigLogo.Source = null;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = 296;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.EndInit();
            img.DownloadFailed += (_, _) => { if (ReferenceEquals(BigLogo.Source, img)) BigLogo.Source = null; };
            img.DecodeFailed += (_, _) => { if (ReferenceEquals(BigLogo.Source, img)) BigLogo.Source = null; };
            BigLogo.Source = img;
        }
        catch
        {
            BigLogo.Source = null;
        }
    }

    private static void AutomationSetName(DependencyObject el, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(el, name);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _tick.Stop();
        StopRecordingIfAny();
        _session?.Dispose();
        _player.Dispose();
        Storage.SaveStations(_stations);
        Storage.SaveSettings(_settings);
    }
}
