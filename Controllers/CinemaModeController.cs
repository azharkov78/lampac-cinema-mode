using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CinemaMode.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Shared;
using Shared.Attributes;
using Shared.Services;

namespace CinemaMode.Controllers;

public class CinemaModeController : BaseController
{
    static readonly SemaphoreSlim ManualRefreshGate = new(1, 1);
    static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromMinutes(5);
    static long LastManualRefreshTicks;

    [HttpGet, AllowAnonymous, Route("cinemamode.js"), Route("cinemamode/js/{token}")]
    [Staticache(cacheMinutes: 10, always: true, setHeadersNoCache: true)]
    public ActionResult Plugin(string token)
    {
        var plugin = FileCache.ReadAllText($"{ModInit.modpath}/plugin.js", "cinemamode.js").Replace("{localhost}", host).Replace("{token}", System.Web.HttpUtility.UrlEncode(token));
        return ContentTo(plugin, "application/javascript; charset=utf-8");
    }

    TrailerPoolManager? PoolManager() => ModInit.Pool;

    [HttpGet, AllowAnonymous, Route("cinemamode/pool")]
    public async Task<ActionResult> Pool()
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("{}", "application/json");
        var index = await pool.LoadAsync().ConfigureAwait(false);
        index.trailers = pool.ReadyEntries(index, ModInit.conf.storage_path);
        return ContentTo(Newtonsoft.Json.JsonConvert.SerializeObject(index), "application/json");
    }

    [HttpGet, AllowAnonymous, Route("cinemamode/random")]
    public ActionResult Random(int? n)
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("[]", "application/json");
        // When the operator has disabled the module, return an empty playlist so Lampa falls through cleanly.
        var conf = ModInit.conf;
        if (!conf.enabled) return ContentTo("[]", "application/json");
        var perRequest = n ?? conf.trailers_per_movie;
        var picked = pool.PickRandom(conf.storage_path, Math.Clamp(perRequest, 1, conf.EffectiveStorageCount()));
        return ContentTo(Newtonsoft.Json.JsonConvert.SerializeObject(picked.Select(t => new { url = $"{host}/{t.file_url}", title = t.title }).ToArray()), "application/json");
    }

    [HttpGet, AllowAnonymous, Route("cinemamode/refresh")]
    public async Task<ActionResult> Refresh(CancellationToken ct)
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("{}", "application/json");
        var conf = ModInit.conf;
        if (!conf.enabled)
        {
            return ContentTo("{\"disabled\":true}", "application/json");
        }
        var sources = conf.EffectiveSources();
        if (sources.Count == 0)
        {
            return ContentTo("{\"error\":\"no_sources\"}", "application/json");
        }
        if (!ManualRefreshGate.Wait(0))
        {
            Response.StatusCode = 202;
            return ContentTo("{\"accepted\":true,\"in_progress\":true}", "application/json");
        }

        var now = DateTime.UtcNow;
        var last = new DateTime(Interlocked.Read(ref LastManualRefreshTicks), DateTimeKind.Utc);
        var remaining = ManualRefreshCooldown - (now - last);
        if (last.Ticks > 0 && remaining > TimeSpan.Zero)
        {
            ManualRefreshGate.Release();
            Response.StatusCode = 429;
            Response.Headers["Retry-After"] = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
            return ContentTo("{\"error\":\"refresh_rate_limited\"}", "application/json");
        }
        Interlocked.Exchange(ref LastManualRefreshTicks, now.Ticks);

        var logger = HttpContext.RequestServices.GetService(typeof(ILogger<CinemaModeController>)) as ILogger<CinemaModeController>;
        _ = Task.Run(async () =>
        {
            try
            {
                await pool.RefreshAsync(sources, conf.EffectiveDownloadCount(), conf.EffectiveStorageCount(), conf.max_height, conf.storage_path, conf.delete_old, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "CinemaMode: manual refresh failed");
            }
            finally
            {
                ManualRefreshGate.Release();
            }
        });
        Response.StatusCode = 202;
        return ContentTo("{\"accepted\":true,\"in_progress\":true}", "application/json");
    }

    [HttpGet, AllowAnonymous, Route("cinemamode/status")]
    public async Task<ActionResult> Status()
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("not_loaded", "text/plain");
        var conf = ModInit.conf;
        var index = await pool.LoadAsync().ConfigureAwait(false); var ready = pool.ReadyEntries(index, conf.storage_path);
        return ContentTo(
            $"enabled={conf.enabled}\n" +
            $"delete_old={conf.delete_old}\n" +
            $"download_count={conf.EffectiveDownloadCount()}\n" +
            $"storage_count={conf.EffectiveStorageCount()}\n" +
            $"trailers_per_movie={conf.trailers_per_movie}\n" +
            $"sources={index.channel}\n" +
            $"ready_count={ready.Count}\n" +
            $"downloaded={ready.Count}\n" +
            $"updated_at={index.updated_at}\n",
            "text/plain");
    }
}
