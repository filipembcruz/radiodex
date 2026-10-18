# Data files

Radiodex keeps everything in plain JSON so it's easy to back up, edit, or read from another app.

Location on Windows: `%AppData%\Radiodex\`

## stations.json

```json
[
  {
    "id": "6f1c…",
    "name": "Rádio CDL 102.9 FM",
    "logoUrl": "https://img.radios.com.br/radio/xl/radio11282_1729878265.jpg",
    "place": "Belo Horizonte / MG",
    "source": "radiosComBr",
    "sourceRef": "https://www.radios.com.br/aovivo/radio-cdl-1029-fm/11282",
    "streamUrl": "https://…",
    "codec": "mp3",
    "bitrate": 128,
    "favorite": true,
    "addedUtc": "2026-10-17T22:00:00Z",
    "linkCheckedUtc": "2026-10-17T22:00:00Z"
  }
]
```

- `source`: `direct`, `radioBrowser`, `radiosComBr` or `radioGarden`.
- `sourceRef`: the page link for radios.com.br / Radio Garden, the `stationuuid` for Radio Browser.

## liked-songs.json

```json
[{ "title": "Trem-Bala", "artist": "Ana Vilela", "stationName": "Rádio CDL 102.9 FM", "likedUtc": "2026-10-17T22:10:00Z" }]
```

## settings.json

```json
{ "language": "pt", "volume": 72, "lastStationId": "6f1c…", "recordingsFolder": "C:\\Users\\you\\Music\\Radiodex" }
```

## Recordings

Saved in the stream's own format (`.mp3`, `.aac`, `.ogg`), named `{Station}_{yyyy-MM-dd_HH-mm-ss}.{ext}`,
with a `.cue` sheet next to it listing each song and the second it started (CUE `INDEX` uses mm:ss:ff, 75 frames per second).
