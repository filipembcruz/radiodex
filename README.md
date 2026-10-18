<p align="center">
  <img src="docs/icon.png" width="112" alt="Radiodex icon">
</p>

<h1 align="center">Radiodex</h1>

<p align="center">
  Internet radio for Windows that keeps working: when a station changes its stream link,<br>
  Radiodex finds the new one by itself. Record shows with every song marked to the second.
</p>

<p align="center">
  <b>English</b> · <a href="README.pt-BR.md">Português</a>
</p>

<p align="center">
  <a href="https://github.com/filipembcruz/radiodex/releases/latest/download/Radiodex.exe"><b>⬇ Download Radiodex.exe</b></a><br>
  <sub>Windows 10 (2004+) or 11 · 64-bit · free and open source (MIT)</sub>
</p>

---

## Install (1 minute)

1. Download **Radiodex.exe** from the [latest release](https://github.com/filipembcruz/radiodex/releases/latest).
2. Run it. There's no installer and nothing else to install: .NET is bundled inside.
   On first launch Radiodex installs itself to `%LocalAppData%\Programs\Radiodex` (no admin rights) and creates
   shortcuts on the **Desktop** and in the **Start menu**. You can delete the downloaded file afterwards.
3. Optional: right-click the shortcut → **Pin to taskbar** (Windows only lets you do this yourself).

> **SmartScreen:** the app isn't code-signed yet, so Windows may show "Windows protected your PC" the first time.
> Click **More info** → **Run anyway**.

To uninstall, delete the folders `%LocalAppData%\Programs\Radiodex` and `%AppData%\Radiodex`, and the two shortcuts.

## Features

**Find stations**
- Search tens of thousands of stations worldwide by name, city or genre ([Radio Browser](https://www.radio-browser.info) catalog), and listen before adding.
- Or paste a link from **radios.com.br**, **Radio Garden**, a playlist (`.m3u`, `.pls`, `.asx`) or a direct stream.

**Links that fix themselves**
- If a stream stops, Radiodex asks the station's source for the current link, then looks the station up in the open catalog, and switches over without you doing anything.
- Retries with back-off (10 s, 30 s, 2 min, then every 5 min) and shows what it's doing.
- **Refresh link** button to check on demand.

**Listening**
- Play / pause, volume, favorites.
- **Rename any station and give it your own cover** (pencil next to the name, or right-click in the list). The name is used in recordings too.
- Shows the song playing now (when the station sends it) and a "recently played" list.
- Uses Windows' own player: the keyboard **media keys** work, and the Windows volume flyout shows the station logo and song. Next/previous switches stations.
- ♥ **Like songs**, then find them under **Liked songs**: search, open on YouTube or Spotify, export to CSV.

**Recording**
- Saves the stream exactly as it arrives (no re-encoding) to `Music\Radiodex`.
- Writes a `.cue` sheet next to each recording with every song and the second it started: players like VLC and foobar2000 show it as a track list.
- If the link changes mid-recording, the recording continues in the same file.
- **Recordings** screen: a timeline of the songs, listen from any song, adjust sync if the titles came early or late,
  like songs, and **split into separate files** per song (no quality loss, MP3s get title/artist tags).

**Languages:** English and Português, switchable live.

## Roadmap

- [ ] Tray icon (keep playing when the window is closed) and Windows notifications
- [ ] Mini player (small, always on top)
- [ ] Settings: start with Windows, recordings folder, source order
- [ ] macOS version (native SwiftUI, same data format)

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```bat
build.bat
```

or manually:

```bat
dotnet publish src\Radiodex\Radiodex.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o out
```

Every push to `main` builds the app on GitHub Actions and publishes a new release with `Radiodex.exe`.

### Project structure

```
src/Radiodex/
  Models/            Station, LikedSong
  Services/
    Sources/         Radio Browser, radios.com.br, Radio Garden, playlists, stream checks
    PlayerService    Windows media player + media keys
    StreamSession    the single connection to the station: relays audio to the player,
                     reads song titles (ICY metadata) and records with a .cue sheet
    Loc              all UI text, English and Portuguese
    Storage          JSON files in %AppData%\Radiodex
  Themes/Dark.xaml   colors and control styles
docs/
  sources.md         how each source is read and how broken links are repaired
  data-format.md     the JSON and recording formats
```

### Translations

All text lives in [`src/Radiodex/Services/Loc.cs`](src/Radiodex/Services/Loc.cs) as English/Portuguese pairs.
Pull requests for more languages are welcome.

### Tech

C# · .NET 8 · WPF · Windows.Media.Playback · no third-party packages.

## Notes

Radiodex plays public streams published by the stations themselves. Recordings are for personal use;
respect the rights of broadcasters and artists in your country.

## License

[MIT](LICENSE)
