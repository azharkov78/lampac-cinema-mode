# CinemaMode

Lampac dynamic module: fetch the latest trailers from `@KinomanTrailers` with
`yt-dlp`, retain a bounded local pool, and serve it to Lampa from Lampac static
files.

## S6 behaviour

- Channel: `@KinomanTrailers` by default.
- Listing: only the latest `pool_size` videos are queried (`10` by default).
- Storage: operator-supplied `storage_path` inside `/opt/lampac/wwwroot`; files
  live in the `cinemamode/` subdirectory (e.g. `<storage>/cinemamode/<id>.mp4`).
- Retention: at most `pool_size` (hard cap 10) CinemaMode-owned `11-char-id.mp4`
  files inside that subdirectory, oldest `mtime` first. Other content in
  `/opt/lampac/wwwroot` is never touched.
- Refresh: every `refresh_minutes` (`360` = six hours) and once at startup when
  `refresh_on_start` is true. The interval is re-read from config between cycles.
- URL: `/trailers/cinemamode/<youtube-id>.mp4` (derived from the configured
  storage path; never escapes `/opt/lampac/wwwroot`).

## Configuration

Lampac reads this from `/opt/lampac/init.conf` via `ModuleInvoke.Init`. The file
is JSON-format despite its `.conf` name; add or edit the top-level `CinemaMode`
object:

```json
"CinemaMode": {
  "enabled": true,
  "sources": ["@KinomanTrailers", "https://www.youtube.com/@AnotherChannel/videos"],
  "pool_size": 10,
  "trailers_per_movie": 3,
  "delete_old": true,
  "storage_path": "/opt/lampac/wwwroot/trailers",
  "ytdlp_path": "/usr/local/bin/yt-dlp",
  "max_height": 1080,
  "refresh_minutes": 360,
  "refresh_on_start": true
}
```

`enabled=false` disables startup/periodic/manual downloads and makes `/random`
return an empty list. `sources` accepts multiple YouTube channel handles or
URLs; an older single `channel` value remains supported when `sources` is empty.
`pool_size` controls the download/retention target and is hard-clamped to `1..10`.
`trailers_per_movie` controls automatic and manual playback. With `delete_old=true`
old CinemaMode-owned files are evicted to the bounded pool; with `false` they are
left on disk. Files outside the CinemaMode-owned subdirectory are never touched.

Endpoints: `/cinemamode.js`, `/cinemamode/pool`, `/cinemamode/random`,
`/cinemamode/status`; `/cinemamode/refresh` is authenticated. `/pool` and
`/random` only return entries whose mp4 exists on disk and is at least 100 KB.

## Verification limitation

S6 has the .NET 10 runtime but no SDK, therefore no `dotnet build` can be run
there. The Lampac dynamic-module loader compiles source when the module loads.
Deployment verification must check `lampac.service`, `/cinemamode/status`, a
manual authenticated refresh, and the on-disk file cap.
