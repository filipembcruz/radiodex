using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Radiodex.Models;
using Radiodex.Services;

namespace Radiodex;

/// <summary>Rename a station and give it your own cover image.</summary>
public partial class EditStationWindow : Window
{
    private readonly Station _station;
    private string? _pickedFile;
    private bool _resetCover;

    public EditStationWindow(Station station)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.UseDarkTitleBar(this);
        _station = station;

        // Stations saved by older versions don't know their original values yet.
        _station.OriginalName ??= _station.Name;
        if (_station.OriginalLogoUrl == null && !IsCustomCover(_station.LogoUrl)) _station.OriginalLogoUrl = _station.LogoUrl;

        NameBox.Text = station.Name;
        CoverInitials.Text = station.Initials;
        ShowCover(station.LogoUrl);
        UpdateResetButtons();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static string CoversFolder => Path.Combine(Storage.Folder, "covers");

    private static bool IsCustomCover(string? url) =>
        url != null && url.StartsWith("file:", StringComparison.OrdinalIgnoreCase);

    private void ShowCover(string? url)
    {
        Cover.Source = null;
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = 256;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            img.EndInit();
            Cover.Source = img;
        }
        catch
        {
            Cover.Source = null;
        }
    }

    private void UpdateResetButtons()
    {
        var hasCustom = _pickedFile != null || (!_resetCover && IsCustomCover(_station.LogoUrl));
        ResetCoverButton.Visibility = hasCustom ? Visibility.Visible : Visibility.Collapsed;

        var original = _station.OriginalName;
        var differs = !string.IsNullOrWhiteSpace(original) && !string.Equals(NameBox.Text.Trim(), original, StringComparison.Ordinal);
        ResetNameButton.Visibility = differs ? Visibility.Visible : Visibility.Collapsed;
        ResetNameButton.Content = Loc.F("ResetName", original ?? "");
    }

    private void OnPickCover(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("ChooseCover"),
            Filter = Loc.T("ImageFilter") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|*.*|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        _pickedFile = dlg.FileName;
        _resetCover = false;
        ShowCover(new Uri(dlg.FileName).AbsoluteUri);
        UpdateResetButtons();
    }

    private void OnResetCover(object sender, RoutedEventArgs e)
    {
        _pickedFile = null;
        _resetCover = true;
        ShowCover(_station.OriginalLogoUrl);
        UpdateResetButtons();
    }

    private void OnResetName(object sender, RoutedEventArgs e)
    {
        NameBox.Text = _station.OriginalName ?? NameBox.Text;
        UpdateResetButtons();
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        Dispatcher.InvokeAsync(UpdateResetButtons);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length > 0) _station.Name = name;

        try
        {
            if (_pickedFile != null)
            {
                Directory.CreateDirectory(CoversFolder);
                // A new file name each time, so Windows doesn't show a cached old picture.
                var dest = Path.Combine(CoversFolder, $"{_station.Id:N}_{DateTime.UtcNow.Ticks}{Path.GetExtension(_pickedFile).ToLowerInvariant()}");
                File.Copy(_pickedFile, dest, overwrite: true);
                DeleteOldCovers(keep: dest);
                _station.LogoUrl = new Uri(dest).AbsoluteUri;
            }
            else if (_resetCover)
            {
                DeleteOldCovers();
                _station.LogoUrl = _station.OriginalLogoUrl;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Radiodex", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void DeleteOldCovers(string? keep = null)
    {
        if (!Directory.Exists(CoversFolder)) return;
        foreach (var f in Directory.GetFiles(CoversFolder, $"{_station.Id:N}_*"))
        {
            if (keep != null && string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(f); } catch { }
        }
    }
}
