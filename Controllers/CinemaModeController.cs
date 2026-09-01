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
        var picked = pool.PickRandom(ModInit.conf.storage_path, Math.Clamp(n ?? ModInit.conf.trailers_per_movie, 1, ModInit.conf.pool_size));
        return ContentTo(Newtonsoft.Json.JsonConvert.SerializeObject(picked.Select(t => new { url = $"{host}/{t.file_url}", title = t.title }).ToArray()), "application/json");
    }

    [HttpGet, Authorize, Route("cinemamode/refresh")]
    public async Task<ActionResult> Refresh(CancellationToken ct)
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("{}", "application/json");
        try { return ContentTo(Newtonsoft.Json.JsonConvert.SerializeObject(await pool.RefreshAsync(ModInit.conf.channel, ModInit.conf.pool_size, ModInit.conf.max_height, ModInit.conf.storage_path, ct).ConfigureAwait(false)), "application/json"); }
        catch (Exception ex)
        {
            var logger = HttpContext.RequestServices.GetService(typeof(ILogger<CinemaModeController>)) as ILogger<CinemaModeController>;
            logger?.LogError(ex, "CinemaMode: manual refresh failed");
            return ContentTo("{\"error\":\"refresh_failed\"}", "application/json");
        }
    }

    [HttpGet, AllowAnonymous, Route("cinemamode/status")]
    public async Task<ActionResult> Status()
    {
        var pool = PoolManager(); if (pool == null || ModInit.conf == null) return ContentTo("not_loaded", "text/plain");
        var index = await pool.LoadAsync().ConfigureAwait(false); var ready = pool.ReadyEntries(index, ModInit.conf.storage_path);
        return ContentTo($"channel={index.channel}\npool_size={ready.Count}\ndownloaded={ready.Count}\nupdated_at={index.updated_at}\n", "text/plain");
    }
}
