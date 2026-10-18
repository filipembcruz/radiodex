using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Radiodex.Models;
using Radiodex.Services;
using IOPath = System.IO.Path;

namespace Radiodex;

/// <summary>One part of a recording: a song from the .cue sheet, or the talk/ads before the first song.</summary>
public sealed class TrackRow : INotifyPropertyChanged
{
    private bool _cut;
    private bool _current;
    private bool _liked;

    public int Number { get; init; }
    public TimeSpan RawStart { get; init; }
    public TimeSpan Length { get; set; }
    public string Title { get; init; } = "";
    public string? Artist { get; init; }
    public bool IsSong { get; init; }
    public string StartLabel { get; set; } = "";

    public string LengthLabel => Length > TimeSpan.Zero ? RecordingsWindow.Fmt(Length) : "";
    public FontWeight TitleWeight => IsSong ? FontWeights.SemiBold : FontWeights.Normal;
    public Brush TitleBrush => (Brush)Application.Current.FindResource(IsSong ? "Text" : "Muted");
    public Brush RowBrush => _current ? (Brush)Application.Current.FindResource("Selected") : (Brush)Application.Current.FindResource("Sidebar");

    public bool Cut { get => _cut; set { _cut = value; On(nameof(Cut)); } }
    public bool Current { get => _current; set { if (_current == value) return; _current = value; On(nameof(RowBrush)); } }
    public bool Liked { get => _liked; set { _liked = value; On(nameof(HeartFill)); On(nameof(HeartStroke)); } }
    public Brush HeartFill => _liked ? (Brush)Application.Current.FindResource("Heart") : Brushes.Transparent;
    public Brush HeartStroke => _liked ? (Brush)Application.Current.FindResource("Heart") : (Brush)Application.Current.FindResource("Muted");

    public void Refresh(params string[] names) { foreach (var n in names) On(n); }
    private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Saved recordings: song timeline, listen from any song, sync adjust, like songs, split into tracks.</summary>
public partial class RecordingsWindow : Window
{
    private readonly string _folder;
    private readonly List<LikedSong> _liked;
    private readonly Action _saveLiked;
    private readonly Action _beforePlay;
    private readonly FilePlayer _player = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<TrackRow> _rows = new();

    private RecordingFile? _file;
    private CueSheet? _cue;
    private TimeSpan _total;
    private int _offset;   // seconds added to every song start (the station's titles are often a bit early or late)
    private bool _playing;
    private readonly string? _recordingNow; // still being written: not listed until it's finished

    public RecordingsWindow(string folder, List<LikedSong> liked, Action saveLiked, Action beforePlay, string? select = null, string? recordingNow = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _folder = folder;
        _liked = liked;
        _saveLiked = saveLiked;
        _beforePlay = beforePlay;
        _recordingNow = recordingNow;
        TrackList.ItemsSource = _rows;

        _player.Opened += d => Dispatcher.InvokeAsync(() => { _total = d; BuildRows(); });
        _player.PlayingChanged += p => Dispatcher.InvokeAsync(() => { _playing = p; UpdatePlayIcon(); });
        _player.Failed += msg => Dispatcher.InvokeAsync(() => FooterInfo.Text = msg);
        _tick.Tick += (_, _) => UpdatePosition();
        _tick.Start();
        TimelineHost.SizeChanged += (_, _) => UpdatePosition();
        Closed += (_, _) => { _tick.Stop(); SaveSyncIfChanged(); _player.Dispose(); };

        LoadList(select);
    }

    public static string Fmt(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    private Brush B(string key) => (Brush)FindResource(key);

    // ---------------- list ----------------

    private void LoadList(string? select)
    {
        var files = RecordingFile.Scan(_folder)
            .Where(f => _recordingNow == null || !string.Equals(f.AudioPath, _recordingNow, StringComparison.OrdinalIgnoreCase))
            .ToList();
        FileList.ItemsSource = files;
        NoRecordings.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var pick = files.FirstOrDefault(f => select != null && string.Equals(f.AudioPath, select, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
        if (pick != null) FileList.SelectedItem = pick;
    }

    private void OnFileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FileList.SelectedItem is not RecordingFile f || ReferenceEquals(f, _file)) return;
        SaveSyncIfChanged();
        _file = f;
        _offset = 0;
        _total = TimeSpan.Zero;
        try { _cue = CueSheet.Load(f.CuePath); } catch { _cue = null; }

        PickHint.Visibility = Visibility.Collapsed;
        Detail.Visibility = Visibility.Visible;
        FileTitle.Text = f.FileName;
        FileDetails.Text = f.Details;
        FooterInfo.Text = Loc.T("SplitHint");
        BuildRows();
        _player.Open(f.AudioPath);
    }

    /// <summary>Right-click selects the recording under the mouse, so the menu acts on it.</summary>
    private void OnFileRightClick(object sender, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListBoxItem) el = VisualTreeHelper.GetParent(el);
        if (el is ListBoxItem item) item.IsSelected = true;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _folder + "\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnDeleteRecording(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not RecordingFile f) return;
        if (MessageBox.Show(this, Loc.F("DeleteRecordingConfirm", f.FileName), "Radiodex", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _player.Close();
        _file = null;
        try
        {
            // To the Recycle Bin, so a mistake can be undone.
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.AudioPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            if (File.Exists(f.CuePath))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f.CuePath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Radiodex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Detail.Visibility = Visibility.Collapsed;
        PickHint.Visibility = Visibility.Visible;
        LoadList(null);
    }

    // ---------------- rows and timeline ----------------

    private void BuildRows()
    {
        _rows.Clear();
        var marks = _cue?.Tracks.OrderBy(t => t.Start).ToList() ?? new List<CueTrack>();
        var station = _cue?.Performer ?? _file?.Station ?? "";

        var rows = new List<TrackRow>();
        var firstStart = marks.Count > 0 ? Shift(marks[0].Start) : TimeSpan.Zero;
        if (marks.Count == 0 || firstStart > TimeSpan.FromSeconds(5))
            rows.Add(new TrackRow { Number = 0, RawStart = TimeSpan.Zero, Title = Loc.T("LegendTalk"), IsSong = false });

        for (var i = 0; i < marks.Count; i++)
        {
            var m = marks[i];
            // Stations often send their own name (or nothing useful) during talk and ads.
            var isSong = !string.Equals(m.Title, station, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(m.Artist, station, StringComparison.OrdinalIgnoreCase) &&
                         m.Title.Length > 0;
            rows.Add(new TrackRow
            {
                Number = i + 1,
                RawStart = m.Start,
                Title = isSong ? m.Title : (m.Title.Length > 0 ? m.Title : Loc.T("LegendTalk")),
                Artist = isSong ? m.Artist : null,
                IsSong = isSong,
                Cut = isSong
            });
        }

        for (var i = 0; i < rows.Count; i++)
        {
            var start = StartOf(rows[i]);
            var end = i + 1 < rows.Count ? StartOf(rows[i + 1]) : _total;
            rows[i].Length = end > start ? end - start : TimeSpan.Zero;
            rows[i].StartLabel = Fmt(start);
            rows[i].Liked = rows[i].IsSong && IsLiked(rows[i]);
        }
        foreach (var r in rows)
        {
            r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TrackRow.Cut)) UpdateSplitButton(); };
            _rows.Add(r);
        }

        NoMarks.Visibility = marks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SyncText.Text = (_offset > 0 ? "+" : "") + _offset + " s";
        BuildTimeline();
        UpdateSplitButton();
        UpdatePosition();
    }

    private TimeSpan Shift(TimeSpan raw) => raw + TimeSpan.FromSeconds(_offset) < TimeSpan.Zero ? TimeSpan.Zero : raw + TimeSpan.FromSeconds(_offset);

    private TimeSpan StartOf(TrackRow r) => r.Number == 0 ? TimeSpan.Zero : Shift(r.RawStart);

    private void BuildTimeline()
    {
        Timeline.Children.Clear();
        Timeline.ColumnDefinitions.Clear();
        if (_total <= TimeSpan.Zero || _rows.Count == 0) return;

        var col = 0;
        foreach (var r in _rows)
        {
            if (r.Length <= TimeSpan.Zero) continue;
            Timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(r.Length.TotalSeconds, GridUnitType.Star) });
            var bar = new Border
            {
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(1, 0, 1, 0),
                Background = new SolidColorBrush(r.IsSong ? (col % 2 == 0 ? Color.FromRgb(0xC9, 0x8A, 0x2E) : Color.FromRgb(0xB5, 0x7A, 0x26)) : Color.FromRgb(0x3A, 0x41, 0x50)),
                ToolTip = $"{r.StartLabel}  {r.Title}" + (r.Artist != null ? " — " + r.Artist : ""),
                Tag = r
            };
            Grid.SetColumn(bar, col++);
            Timeline.Children.Add(bar);
        }
    }

    private void UpdatePosition()
    {
        if (_file == null) return;
        var pos = _player.Position;
        TimeText.Text = $"{Fmt(pos)} / {Fmt(_total)}";

        TrackRow? current = null;
        foreach (var r in _rows) if (pos >= StartOf(r)) current = r;
        foreach (var r in _rows) r.Current = ReferenceEquals(r, current);
        CurrentTrack.Text = current == null ? "—" : current.Title + (current.Artist != null ? "  ·  " + current.Artist : "");

        foreach (var child in Timeline.Children.OfType<Border>())
            child.BorderBrush = ReferenceEquals(child.Tag, current) ? B("Text") : null;
        foreach (var child in Timeline.Children.OfType<Border>())
            child.BorderThickness = ReferenceEquals(child.Tag, current) ? new Thickness(2) : new Thickness(0);

        var w = TimelineHost.ActualWidth;
        var x = _total > TimeSpan.Zero ? pos.TotalSeconds / _total.TotalSeconds * w : 0;
        Canvas.SetLeft(Playhead, Math.Clamp(x - 1, 0, Math.Max(0, w - 2)));
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Data = (Geometry)FindResource(_playing ? "IcoPause" : "IcoPlay");
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, Loc.T(_playing ? "Pause" : "Play"));
    }

    private void UpdateSplitButton()
    {
        var n = _rows.Count(r => r.IsSong && r.Cut);
        SplitButton.Content = Loc.F("SplitTracks", n);
        // Cutting without re-encoding works for MP3 and AAC (ADTS); Ogg/FLAC pieces would need their headers rebuilt.
        var ext = _file == null ? "" : IOPath.GetExtension(_file.AudioPath).ToLowerInvariant();
        SplitButton.IsEnabled = n > 0 && _total > TimeSpan.Zero && ext is ".mp3" or ".aac";
    }

    // ---------------- playback ----------------

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_file == null) return;
        if (_playing) _player.Pause();
        else { _beforePlay(); _player.Play(); }
    }

    private void SeekAndPlay(TimeSpan t)
    {
        _beforePlay();
        try { _player.Position = t; } catch { /* not loaded yet: start from the beginning */ }
        _player.Play();
        UpdatePosition();
    }

    private void OnListenFrom(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackRow r) SeekAndPlay(StartOf(r));
    }

    private void OnTimelineClick(object sender, MouseButtonEventArgs e)
    {
        if (_total <= TimeSpan.Zero || TimelineHost.ActualWidth <= 0) return;
        var x = e.GetPosition(TimelineHost).X;
        SeekAndPlay(TimeSpan.FromSeconds(_total.TotalSeconds * Math.Clamp(x / TimelineHost.ActualWidth, 0, 1)));
    }

    // ---------------- sync, likes, split ----------------

    private void OnSyncDown(object sender, RoutedEventArgs e) => ChangeSync(-1);
    private void OnSyncUp(object sender, RoutedEventArgs e) => ChangeSync(+1);

    private void ChangeSync(int delta)
    {
        _offset = Math.Clamp(_offset + delta, -60, 60);
        var cuts = _rows.Where(r => r.IsSong).Select(r => (r.Number, r.Cut)).ToDictionary(x => x.Number, x => x.Cut);
        BuildRows();
        foreach (var r in _rows) if (cuts.TryGetValue(r.Number, out var c)) r.Cut = c;
    }

    /// <summary>Writes the adjusted times back into the .cue so other players see them too.</summary>
    private void SaveSyncIfChanged()
    {
        if (_offset == 0 || _cue == null || _file == null) return;
        foreach (var t in _cue.Tracks) t.Start = Shift(t.Start);
        try { _cue.Save(_file.CuePath); } catch { }
        _offset = 0;
    }

    private bool IsLiked(TrackRow r) =>
        _liked.Any(l => string.Equals(l.Title, r.Title, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(l.Artist ?? "", r.Artist ?? "", StringComparison.OrdinalIgnoreCase));

    private void OnLikeTrack(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrackRow r || !r.IsSong) return;
        var existing = _liked.FirstOrDefault(l => string.Equals(l.Title, r.Title, StringComparison.OrdinalIgnoreCase) &&
                                                   string.Equals(l.Artist ?? "", r.Artist ?? "", StringComparison.OrdinalIgnoreCase));
        if (existing != null) _liked.Remove(existing);
        else _liked.Add(new LikedSong { Title = r.Title, Artist = r.Artist, StationName = _cue?.Performer ?? _file?.Station ?? "" });
        _saveLiked();
        r.Liked = existing == null;
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        if (_file == null || _total <= TimeSpan.Zero) return;
        UpdateSplitButton();
        var pieces = new List<TrackSplitter.Piece>();
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (!r.IsSong || !r.Cut || r.Length <= TimeSpan.Zero) continue;
            var start = StartOf(r);
            pieces.Add(new TrackSplitter.Piece { Number = r.Number, Start = start, End = start + r.Length, Title = r.Title, Artist = r.Artist });
        }
        if (pieces.Count == 0) return;

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var outDir = TrackSplitter.Split(_file.AudioPath, _total, pieces, _cue?.Performer ?? _file.Station);
            FooterInfo.Text = Loc.F("SplitDone", pieces.Count, IOPath.GetFileName(outDir));
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + outDir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Radiodex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }
}
