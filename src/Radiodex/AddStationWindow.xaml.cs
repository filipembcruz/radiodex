using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Radiodex.Models;
using Radiodex.Services;
using Radiodex.Services.Sources;

namespace Radiodex;

public sealed class ResultItem : INotifyPropertyChanged
{
    private bool _added;
    public StationCandidate Candidate { get; init; } = new();
    public bool Added
    {
        get => _added;
        set
        {
            _added = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ButtonText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanAdd)));
        }
    }
    public string ButtonText => Loc.T(Added ? "Added" : "Add");
    public bool CanAdd => !Added;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class AddStationWindow : Window
{
    private readonly Func<StationCandidate, bool, Station> _add;
    private readonly Func<StationCandidate, bool> _isAdded;
    private readonly ObservableCollection<ResultItem> _results = new();
    private PlayerService? _preview;
    private CancellationTokenSource? _cts;
    private StationCandidate? _resolved;
    private bool _linkMode;

    /// <summary>The station to start playing when the dialog closes, if any.</summary>
    public Station? PlayNow { get; private set; }

    public AddStationWindow(Func<StationCandidate, bool, Station> add, Func<StationCandidate, bool> isAdded, string initial)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _add = add;
        _isAdded = isAdded;
        Results.ItemsSource = _results;
        Closed += (_, _) => { _cts?.Cancel(); _preview?.Dispose(); };

        Query.Text = initial;
        SetMode(StationResolver.LooksLikeLink(initial));
        Loaded += (_, _) =>
        {
            Query.Focus();
            Query.CaretIndex = Query.Text.Length;
            if (initial.Length > 0) _ = GoAsync();
        };
    }

    private Brush B(string key) => (Brush)FindResource(key);

    private void SetMode(bool link)
    {
        _linkMode = link;
        TabSearch.Background = link ? Brushes.Transparent : B("Selected");
        TabLink.Background = link ? B("Selected") : Brushes.Transparent;
        TabSearch.Foreground = B(link ? "Muted" : "Text");
        TabLink.Foreground = B(link ? "Text" : "Muted");
        Query.Tag = Loc.T(link ? "LinkPlaceholder" : "SearchPlaceholder");
        GoButton.Content = Loc.T(link ? "Check" : "Search");
        Results.Visibility = link ? Visibility.Collapsed : Visibility.Visible;
        LinkFooter.Visibility = link ? Visibility.Visible : Visibility.Collapsed;
        Preview.Visibility = link && _resolved != null ? Visibility.Visible : Visibility.Collapsed;
        Info.Foreground = B("Muted");
        Info.Text = "";
    }

    private void OnTabSearch(object sender, RoutedEventArgs e) => SetMode(false);
    private void OnTabLink(object sender, RoutedEventArgs e) => SetMode(true);

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = GoAsync();
        else if (e.Key == Key.Escape) Close();
    }

    private void OnGo(object sender, RoutedEventArgs e) => _ = GoAsync();

    private async Task GoAsync()
    {
        var text = Query.Text.Trim();
        if (text.Length == 0) return;

        // Pasting a link into the search tab switches to the link tab.
        if (!_linkMode && StationResolver.LooksLikeLink(text)) SetMode(true);

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        GoButton.IsEnabled = false;

        try
        {
            if (_linkMode) await ResolveAsync(text, ct);
            else await SearchAsync(text, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Info.Text = Loc.F("NetworkError", ex.Message);
        }
        finally
        {
            if (!ct.IsCancellationRequested) GoButton.IsEnabled = true;
        }
    }

    private async Task SearchAsync(string text, CancellationToken ct)
    {
        Info.Text = Loc.T("Searching");
        _results.Clear();
        var found = await RadioBrowser.SearchAsync(text, null, ct);
        foreach (var c in found)
            _results.Add(new ResultItem { Candidate = c, Added = _isAdded(c) });
        Info.Text = found.Count == 0 ? Loc.T("NoResults") : Loc.F("ResultsCount", found.Count);
    }

    private async Task ResolveAsync(string text, CancellationToken ct)
    {
        _resolved = null;
        SaveButton.IsEnabled = false;
        Preview.Visibility = Visibility.Collapsed;
        Info.Foreground = B("Muted");
        Info.Text = Loc.T("Resolving");

        var c = await StationResolver.ResolveLinkAsync(text, ct);
        if (c == null)
        {
            Info.Text = Loc.T("NotFoundInLink");
            return;
        }

        _resolved = c;
        Info.Foreground = B("Live");
        Info.Text = Loc.F("Recognized", StationResolver.SourceName(c.Source));
        PreviewName.Text = c.Name;
        PreviewDetails.Text = c.Details;
        PreviewUrl.Text = c.StreamUrl;
        PreviewLogo.Source = null;
        if (c.LogoUrl != null && Uri.TryCreate(c.LogoUrl, UriKind.Absolute, out var uri))
        {
            try { PreviewLogo.Source = new BitmapImage(uri); } catch { }
        }
        Preview.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
    }

    private void OnSaveLink(object sender, RoutedEventArgs e)
    {
        if (_resolved == null) return;
        PlayNow = _add(_resolved, FavCheck.IsChecked == true);
        Close();
    }

    private void OnAddResult(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultItem item || item.Added) return;
        var st = _add(item.Candidate, false);
        item.Added = true;
        PlayNow ??= st;
    }

    private void OnPreview(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ResultItem item) return;
        _preview ??= new PlayerService();
        _preview.Play(item.Candidate.StreamUrl, item.Candidate.Name, item.Candidate.LogoUrl);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
