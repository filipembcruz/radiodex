using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Radiodex.Models;
using Radiodex.Services;

namespace Radiodex;

/// <summary>The songs you hearted, with quick search on YouTube/Spotify and CSV export.</summary>
public partial class LikedSongsWindow : Window
{
    private readonly List<LikedSong> _all;
    private readonly Action _save;
    private readonly ObservableCollection<LikedSong> _shown = new();

    public LikedSongsWindow(List<LikedSong> liked, Action save)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _all = liked;
        _save = save;
        SearchBox.Tag = Loc.T("SearchSongs");
        Songs.ItemsSource = _shown;
        Refresh();
    }

    private void Refresh()
    {
        var q = SearchBox.Text.Trim();
        _shown.Clear();
        foreach (var s in _all.OrderByDescending(x => x.LikedUtc))
        {
            if (q.Length > 0 &&
                !(s.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                  (s.Artist ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                  s.StationName.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
                continue;
            _shown.Add(s);
        }
        CountText.Text = Loc.F("LikedCount", _all.Count);
        EmptyText.Visibility = _all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Refresh();

    private static LikedSong? SongOf(object sender) => (sender as FrameworkElement)?.DataContext as LikedSong;

    private static string Query(LikedSong s) => string.IsNullOrWhiteSpace(s.Artist) ? s.Title : $"{s.Artist} {s.Title}";

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OnYouTube(object sender, RoutedEventArgs e)
    {
        if (SongOf(sender) is { } s) Open("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(Query(s)));
    }

    private void OnSpotify(object sender, RoutedEventArgs e)
    {
        if (SongOf(sender) is { } s) Open("https://open.spotify.com/search/" + Uri.EscapeDataString(Query(s)));
    }

    private void OnUnlike(object sender, RoutedEventArgs e)
    {
        if (SongOf(sender) is not { } s) return;
        _all.Remove(s);
        _save();
        Refresh();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            FileName = "radiodex-liked-songs.csv",
            Filter = "CSV|*.csv",
            DefaultExt = ".csv"
        };
        if (dlg.ShowDialog(this) != true) return;

        static string Csv(string? v)
        {
            v ??= "";
            return v.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
        }

        var sb = new StringBuilder();
        sb.AppendLine("title,artist,station,liked");
        foreach (var s in _all.OrderByDescending(x => x.LikedUtc))
            sb.AppendLine(string.Join(",", Csv(s.Title), Csv(s.Artist), Csv(s.StationName), s.LikedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Radiodex", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
