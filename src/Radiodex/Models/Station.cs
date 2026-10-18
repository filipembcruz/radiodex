using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Radiodex.Models;

/// <summary>Where a station came from, so its stream link can be found again when it breaks.</summary>
public enum StationSource
{
    Direct,
    RadioBrowser,
    RadiosComBr,
    RadioGarden
}

public sealed class Station : INotifyPropertyChanged
{
    private string _name = "";
    private string? _logoUrl;
    private string _streamUrl = "";
    private bool _favorite;
    private string _status = "";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Notify(nameof(Name));
            Notify(nameof(Initials));
        }
    }

    public string? LogoUrl { get => _logoUrl; set => Set(ref _logoUrl, string.IsNullOrWhiteSpace(value) ? null : value); }

    /// <summary>The logo the source gave us, kept so a custom cover can be undone.</summary>
    public string? OriginalLogoUrl { get; set; }

    /// <summary>The name the source gave us, kept so a rename can be undone.</summary>
    public string? OriginalName { get; set; }

    public string? Place { get; set; }

    public StationSource Source { get; set; } = StationSource.Direct;

    /// <summary>Page URL (radios.com.br, Radio Garden) or station UUID (Radio Browser).</summary>
    public string? SourceRef { get; set; }

    public string StreamUrl { get => _streamUrl; set => Set(ref _streamUrl, value); }

    public string? Codec { get; set; }

    public int Bitrate { get; set; }

    public bool Favorite { get => _favorite; set => Set(ref _favorite, value); }

    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LinkCheckedUtc { get; set; }

    /// <summary>Transient status line shown in the list ("Online", "Link updated").</summary>
    [JsonIgnore]
    public string Status { get => _status; set => Set(ref _status, value); }

    [JsonIgnore]
    public string Initials
    {
        get
        {
            var parts = Name.Split(new[] { ' ', '-', '.' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var w = p.Trim();
                if (w.Length == 0) continue;
                var lower = w.ToLowerInvariant();
                if (lower is "radio" or "rádio" or "fm" or "am" or "web" or "the") continue;
                return w.Length <= 3 ? w.ToUpperInvariant() : w.Substring(0, 3).ToUpperInvariant();
            }
            return Name.Length >= 2 ? Name.Substring(0, 2).ToUpperInvariant() : "FM";
        }
    }

    [JsonIgnore]
    public string FormatText
    {
        get
        {
            var codec = string.IsNullOrWhiteSpace(Codec) ? "" : Codec!.ToUpperInvariant();
            if (Bitrate > 0) return codec.Length > 0 ? $"{codec} · {Bitrate} kbps" : $"{Bitrate} kbps";
            return codec;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Notify(name);
    }
}

/// <summary>A song the user hearted while listening.</summary>
public sealed class LikedSong
{
    public string Title { get; set; } = "";
    public string? Artist { get; set; }
    public string StationName { get; set; } = "";
    public DateTime LikedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string When => LikedUtc.ToLocalTime().ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
}

/// <summary>A station found by a search or by resolving a link, before it is saved.</summary>
public sealed class StationCandidate
{
    public string Name { get; set; } = "";
    public string? LogoUrl { get; set; }
    public string? Place { get; set; }
    public string StreamUrl { get; set; } = "";
    public string? Codec { get; set; }
    public int Bitrate { get; set; }
    public StationSource Source { get; set; }
    public string? SourceRef { get; set; }

    public string Details
    {
        get
        {
            var bits = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(Place)) bits.Add(Place!);
            var fmt = string.IsNullOrWhiteSpace(Codec) ? "" : Codec!.ToUpperInvariant();
            if (Bitrate > 0) fmt = (fmt + " " + Bitrate + " kbps").Trim();
            if (fmt.Length > 0) bits.Add(fmt);
            return string.Join(" · ", bits);
        }
    }

    public Station ToStation() => new()
    {
        Name = Name,
        OriginalName = Name,
        LogoUrl = LogoUrl,
        OriginalLogoUrl = LogoUrl,
        Place = Place,
        StreamUrl = StreamUrl,
        Codec = Codec,
        Bitrate = Bitrate,
        Source = Source,
        SourceRef = SourceRef,
        LinkCheckedUtc = DateTime.UtcNow
    };
}
