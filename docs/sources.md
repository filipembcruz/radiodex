# Station sources

How Radiodex turns a link or a search into a stream URL, and how it finds a new
link when a stream stops working. Any port (for example a future macOS app) should
follow the same rules.

## Radio Browser (search)

Open, community-run catalog with a public API: <https://api.radio-browser.info>.

- Servers: `de1`, `de2`, `nl1`, `at1`, `fi1` and `all` `.api.radio-browser.info`, tried in order.
- Search: `GET /json/stations/search?{name|state|tag}={query}&limit=40&hidebroken=true&order=clickcount&reverse=true`.
  Radiodex searches by name first, then by state/city and by tag (genre), merging results.
- Refresh: `GET /json/stations/byuuid/{stationuuid}` and use `url_resolved`.
- After a station starts playing, `GET /json/url/{stationuuid}` counts the click (the API asks clients to do this).
- Send a descriptive `User-Agent` (`Radiodex/x.y`).

## radios.com.br (paste link)

- Link: `https://www.radios.com.br/aovivo/{slug}/{id}`.
- The page loads its player with JavaScript, but each station has public playlists:
  `https://www.radios.com.br/play/playlist/{id}/listen-radio.m3u` (also `.pls`, `.asx`).
- Name and logo: the page's `og:title` and `og:image`.

## Radio Garden (paste link)

The site has no official public API; these endpoints are what the site itself uses and may change.

- Link: `https://radio.garden/listen/{slug}/{channelId}`.
- Details: `GET https://radio.garden/api/ara/content/channel/{channelId}` → `data.title`, `data.place.title`, `data.country.title`.
- Stream: `https://radio.garden/api/ara/content/listen/{channelId}/channel.mp3`, which redirects to the station's server.

## Direct links

- `.m3u`, `.pls`, `.asx` playlists are downloaded and the first working URL is used.
- Anything else is opened as a stream. The `icy-name` header gives the station name.
- If the link is a normal web page, Radiodex looks for stream-like URLs in its HTML.

## When a stream fails

1. Ask the station's own source again (Radio Browser by UUID, or re-open the radios.com.br / Radio Garden page).
2. If that gives the same link, check whether the current link answers at all.
3. Search Radio Browser for a station with the same name and try its links.
4. Retry automatically with back-off: 10 s, 30 s, 2 min, then every 5 min.

A link "works" when it answers with a non-HTML content type and sends audio bytes within 10 seconds.

## Playback relay

Radiodex opens one connection to the station and passes the audio to Windows' player through
`http://127.0.0.1:{port}/live.{mp3|aac|ogg}`. That connection also reads the song titles and feeds recordings.

- Content types such as `audio/aacp` (AAC+, common on Icecast-KH servers) are served as `audio/aac`,
  which Windows' player recognises. Played directly, those streams stay on "connecting".
- Servers that allow one listener per IP keep working, since there is only one connection.
- A short server drop is bridged by reconnecting underneath the player.
- HLS (`.m3u8`) and very old SHOUTcast v1 servers (`ICY 200 OK`) are played directly by Windows instead.
