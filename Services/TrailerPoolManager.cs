using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CinemaMode.Models;

namespace CinemaMode.Services;

/// <summary>Maintains the bounded, CinemaMode-owned trailer pool on S6.</summary>
public class TrailerPoolManager
{
    const string DataDirectory = "/opt/lampac/database/cinemamode";
    const string WwwRoot = "/opt/lampac/wwwroot";
    const string OwnedSubdirectory = "cinemamode";
    const long MinPlayableBytes = 100_000;
    const int HardCountCap = 100;
    static readonly Regex YouTubeId = new("^[A-Za-z0-9_-]{11}$", RegexOptions.Compiled);
    static readonly Regex Handle = new("^@[A-Za-z0-9._-]{1,40}$", RegexOptions.Compiled);
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly ILogger<TrailerPoolManager> _log;
    private readonly YtDlpRunner _ytdlp;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrailerPoolManager(ILogger<TrailerPoolManager> log, YtDlpRunner ytdlp) { _log = log; _ytdlp = ytdlp; }
    public string PoolFilePath => Path.Combine(DataDirectory, "pool.json");

    /// <summary>Resolves the absolute storage path; always appends the CinemaMode-owned subdirectory.</summary>
    public string ResolveStoragePath(string configured)
    {
        var basePath = string.IsNullOrWhiteSpace(configured) ? Path.Combine(WwwRoot, "trailers") : Path.GetFullPath(configured);
        var path = Path.Combine(basePath, OwnedSubdirectory);
        var root = WwwRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.Ordinal) && !string.Equals(path, WwwRoot, StringComparison.Ordinal))
            throw new ArgumentException("CinemaMode storage_path must be inside /opt/lampac/wwwroot", nameof(configured));
        Directory.CreateDirectory(path);
        return path;
    }

    public string BuildFileUrl(string storagePath, string id)
    {
        if (!YouTubeId.IsMatch(id)) throw new ArgumentException("invalid video id", nameof(id));
        var storage = ResolveStoragePath(storagePath);
        var relative = Path.GetRelativePath(WwwRoot, Path.Combine(storage, id + ".mp4")).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") throw new InvalidOperationException("storage path escaped wwwroot");
        return relative;
    }

    public async Task<PoolIndex> LoadAsync()
    {
        if (!File.Exists(PoolFilePath)) return new PoolIndex();
        try
        {
            await using var fs = File.OpenRead(PoolFilePath);
            return await JsonSerializer.DeserializeAsync<PoolIndex>(fs, JsonOpts).ConfigureAwait(false) ?? new PoolIndex();
        }
        catch (Exception ex) { _log.LogWarning(ex, "CinemaMode: pool index unreadable"); return new PoolIndex(); }
    }

    async Task SaveAsync(PoolIndex index)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = PoolFilePath + ".tmp";
        await using (var fs = File.Create(temp)) await JsonSerializer.SerializeAsync(fs, index, JsonOpts).ConfigureAwait(false);
        File.Move(temp, PoolFilePath, overwrite: true);
    }

    static string ChannelListUrl(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("channel is empty", nameof(channel));
        channel = channel.Trim();
        if (channel.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || channel.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return channel;
        return Handle.IsMatch(channel) ? "https://www.youtube.com/" + channel + "/videos" : "https://www.youtube.com/@" + channel.TrimStart('@') + "/videos";
    }

    async Task<List<TrailerRecord>> FetchLatestAsync(string channel, int count, string storagePath, CancellationToken ct)
    {
        var json = await _ytdlp.RunJsonAsync(new[] { "--flat-playlist", "--no-warnings", "--no-progress", "--playlist-end", count.ToString(), "-J", ChannelListUrl(channel) }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json)) return new List<TrailerRecord>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var entries = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : (doc.RootElement.TryGetProperty("entries", out var value) ? value : default);
            if (entries.ValueKind != JsonValueKind.Array) return new List<TrailerRecord>();
            var result = new List<TrailerRecord>();
            foreach (var entry in entries.EnumerateArray())
            {
                var id = entry.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || !YouTubeId.IsMatch(id)) continue;
                var title = entry.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "";
                var author = entry.TryGetProperty("channel", out var channelElement) ? channelElement.GetString() ?? "" : (entry.TryGetProperty("uploader", out var uploader) ? uploader.GetString() ?? "" : "");
                var date = entry.TryGetProperty("upload_date", out var dateElement) ? dateElement.GetString() ?? "" : "";
                var duration = entry.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number ? (int)Math.Round(durationElement.GetDouble()) : 0;
                result.Add(new TrailerRecord { id = id, title = title, author = author, upload_date = date, duration_seconds = duration, file_url = BuildFileUrl(storagePath, id) });
            }
            return result.Take(count).ToList();
        }
        catch (JsonException ex) { _log.LogWarning(ex, "CinemaMode: invalid yt-dlp playlist JSON"); return new List<TrailerRecord>(); }
    }

    /// <summary>Only files inside the owned subdirectory count as CinemaMode-managed and are subject to retention.</summary>
    static bool IsOwnedFile(FileInfo file) => file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
        && file.DirectoryName is string dir
        && Path.GetFileName(dir).Equals(OwnedSubdirectory, StringComparison.Ordinal)
        && YouTubeId.IsMatch(Path.GetFileNameWithoutExtension(file.Name));

    public List<TrailerRecord> ReadyEntries(PoolIndex index, string storagePath)
    {
        var storage = ResolveStoragePath(storagePath);
        return index.trailers.Where(t =>
        {
            if (!YouTubeId.IsMatch(t.id) || string.IsNullOrEmpty(t.downloaded_at)) return false;
            var path = Path.Combine(storage, t.id + ".mp4");
            return File.Exists(path) && new FileInfo(path).Length >= MinPlayableBytes;
        }).ToList();
    }

    /// <summary>True when the trailer pool is enabled. Callers must honour this and short-circuit.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Refreshes the pool by pulling the latest trailers from each configured source, deduping by YouTube id,
    /// downloading at most <paramref name="downloadCount"/> new entries, and (optionally) evicting oldest
    /// owned files until <paramref name="storageCount"/> remains. Both counts are bounded to 1..100.
    /// </summary>
    public async Task<PoolIndex> RefreshAsync(IReadOnlyList<string> sources, int downloadCount, int storageCount, int maxHeight, string storagePath, bool deleteOld, CancellationToken ct)
    {
        downloadCount = Math.Clamp(downloadCount, 1, HardCountCap);
        storageCount = Math.Clamp(storageCount, 1, HardCountCap);
        maxHeight = maxHeight <= 0 ? 1080 : maxHeight;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var storage = ResolveStoragePath(storagePath);
            var cleanSources = (sources ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleanSources.Count == 0)
            {
                _log.LogWarning("CinemaMode: refresh skipped because no sources configured");
                return await LoadAsync().ConfigureAwait(false);
            }

            // Fetch enough recent entries to fill storage_count while respecting the per-refresh download budget.
            var merged = new Dictionary<string, TrailerRecord>(StringComparer.Ordinal);
            foreach (var src in cleanSources)
            {
                ct.ThrowIfCancellationRequested();
                var fetched = await FetchLatestAsync(src, Math.Max(downloadCount, storageCount), storagePath, ct).ConfigureAwait(false);
                foreach (var trailer in fetched)
                {
                    if (merged.TryGetValue(trailer.id, out var existing))
                    {
                        // Keep the newest upload_date when the same video appears on multiple sources.
                        if (UploadDateComparer.Instance.Compare(trailer.upload_date, existing.upload_date) > 0)
                        {
                            merged[trailer.id] = trailer;
                        }
                    }
                    else
                    {
                        merged[trailer.id] = trailer;
                    }
                }
            }

            // upload_date is YYYYMMDD so ordinal string comparison matches newest-first when sorted descending.
            var latest = merged.Values
                .OrderByDescending(t => t.upload_date, UploadDateComparer.Instance)
                .Take(storageCount)
                .ToList();

            if (latest.Count == 0) { _log.LogWarning("CinemaMode: refresh skipped because latest playlist is empty"); return await LoadAsync().ConfigureAwait(false); }

            var existingIndex = await LoadAsync().ConfigureAwait(false);
            var byId = existingIndex.trailers.Where(t => YouTubeId.IsMatch(t.id)).GroupBy(t => t.id).ToDictionary(g => g.Key, g => g.First());
            var next = new PoolIndex { channel = string.Join(",", cleanSources), updated_at = DateTime.UtcNow.ToString("o"), trailers = new List<TrailerRecord>() };
            var downloadedThisRefresh = 0;
            var budgetLogged = false;
            foreach (var trailer in latest)
            {
                var target = Path.Combine(storage, trailer.id + ".mp4");
                if (File.Exists(target) && new FileInfo(target).Length >= MinPlayableBytes)
                {
                    if (byId.TryGetValue(trailer.id, out var prior)) trailer.downloaded_at = prior.downloaded_at;
                    if (string.IsNullOrEmpty(trailer.downloaded_at)) trailer.downloaded_at = File.GetLastWriteTimeUtc(target).ToString("o");
                    trailer.size_bytes = new FileInfo(target).Length; next.trailers.Add(trailer); continue;
                }
                if (downloadedThisRefresh >= downloadCount)
                {
                    if (!budgetLogged)
                    {
                        _log.LogInformation("CinemaMode: download budget reached ({Count})", downloadCount);
                        budgetLogged = true;
                    }
                    continue;
                }
                var result = await _ytdlp.DownloadAsync("https://www.youtube.com/watch?v=" + trailer.id, target, maxHeight, ct).ConfigureAwait(false);
                if (!result.ok) { _log.LogWarning("CinemaMode: download failed for {Id}: {Error}", trailer.id, result.stderr); continue; }
                downloadedThisRefresh++;
                trailer.downloaded_at = DateTime.UtcNow.ToString("o"); trailer.size_bytes = new FileInfo(target).Length; next.trailers.Add(trailer);
            }

            // Retention only ever touches files inside the owned subdirectory. When delete_old is false we
            // still leave files outside that subdirectory alone but skip the in-subdir eviction entirely.
            if (deleteOld)
            {
                var owned = Directory.EnumerateFiles(storage, "*.mp4").Select(path => new FileInfo(path)).Where(IsOwnedFile).OrderBy(f => f.LastWriteTimeUtc).ToList();
                while (owned.Count > storageCount)
                {
                    var victim = owned[0]; victim.Delete(); owned.RemoveAt(0);
                    next.trailers.RemoveAll(t => string.Equals(t.id, Path.GetFileNameWithoutExtension(victim.Name), StringComparison.Ordinal));
                    _log.LogInformation("CinemaMode: retention evicted {File}", victim.Name);
                }
            }
            else
            {
                _log.LogInformation("CinemaMode: delete_old=false, retaining all owned files in {Dir}", storage);
            }

            next.trailers = ReadyEntries(next, storagePath);
            await SaveAsync(next).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Legacy single-channel overload kept for backward compatibility.</summary>
    public Task<PoolIndex> RefreshAsync(string channel, int poolSize, int maxHeight, string storagePath, CancellationToken ct)
        => RefreshAsync(new[] { channel }, poolSize, poolSize, maxHeight, storagePath, deleteOld: true, ct);

    public List<TrailerRecord> PickRandom(string storagePath, int count, PoolIndex? index = null)
    {
        if (!IsEnabled) return new List<TrailerRecord>();
        index ??= LoadAsync().GetAwaiter().GetResult();
        return ReadyEntries(index, storagePath).OrderBy(_ => Random.Shared.Next()).Take(Math.Max(1, count)).ToList();
    }

    sealed class UploadDateComparer : IComparer<string>
    {
        public static readonly UploadDateComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            // YouTube upload_date is "YYYYMMDD"; empty dates sort as oldest so real dates win.
            var xEmpty = string.IsNullOrEmpty(x); var yEmpty = string.IsNullOrEmpty(y);
            if (xEmpty && yEmpty) return 0;
            if (xEmpty) return -1;
            if (yEmpty) return 1;
            return string.Compare(x, y, StringComparison.Ordinal);
        }
    }
}
