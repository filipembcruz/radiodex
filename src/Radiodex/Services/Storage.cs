using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radiodex.Models;

namespace Radiodex.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = "";
    public int Volume { get; set; } = 72;
    public Guid? LastStationId { get; set; }
    public string RecordingsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Radiodex");
}

/// <summary>
/// Plain JSON files in %AppData%\Radiodex. The formats are documented in docs/data-format.md
/// so other ports (macOS) can read the same files.
/// </summary>
public static class Storage
{
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Radiodex");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string PathOf(string file) => System.IO.Path.Combine(Folder, file);

    public static List<Station> LoadStations() => Load<List<Station>>("stations.json") ?? new List<Station>();
    public static void SaveStations(IEnumerable<Station> stations) => Save("stations.json", stations);

    public static AppSettings LoadSettings() => Load<AppSettings>("settings.json") ?? new AppSettings();
    public static void SaveSettings(AppSettings s) => Save("settings.json", s);

    public static List<LikedSong> LoadLikedSongs() => Load<List<LikedSong>>("liked-songs.json") ?? new List<LikedSong>();
    public static void SaveLikedSongs(IEnumerable<LikedSong> songs) => Save("liked-songs.json", songs);

    private static T? Load<T>(string file) where T : class
    {
        try
        {
            var p = PathOf(file);
            if (!File.Exists(p)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(p), Json);
        }
        catch
        {
            return null;
        }
    }

    private static void Save<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var p = PathOf(file);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, p, overwrite: true);
        }
        catch
        {
            // Saving is best effort; a failed write must never crash playback.
        }
    }
}
