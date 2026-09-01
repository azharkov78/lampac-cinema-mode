# CinemaMode Kanban

## S6 trailer pool — pre-deploy static checks
- [x] ModuleConf inherits `ModuleBaseConf` (LampacApk / Tracks style); no `Iproxy`.
- [x] ModInit implements only `IModuleLoaded`; no `IModuleConfigure`. DI factory injected via `initspace.app.ApplicationServices`.
- [x] Background refresh is a `Task.Run` loop in `Loaded()`, not a hosted service. Cancellation through `Dispose`.
- [x] TrailerPoolManager obtained via `ModInit.Pool` static accessor — no DI registration needed.
- [x] Storage path anchored to `<wwwroot>/trailers/cinemamode/`, owned-marker via parent directory.
- [x] Download and storage counts are independently configurable and clamped 1..100 (`HardCountCap`).
- [x] Ready entries require file present AND `Length >= 100 KB`.
- [x] Process timeout 5 min, child tree kill + reap on cancel.
- [x] Refresh interval re-read between cycles.
- [x] Static verification: manifest JSON parses; source braces balance; `node --check plugin.js`; no YoutubeExplode reference remains.
- [!] Independent CLI reviewer was blocked by consent policy; no autonomous reviewer result claimed.
- [x] Approved deploy with backup and Lampac restart (stage6).
- [x] Live verification: service active; module loaded; refresh succeeded; `/cinemamode/status`; 10 owned mp4 files.
