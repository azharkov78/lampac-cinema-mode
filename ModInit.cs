using System;
using System.Collections.Generic;
using System.IO;

using System.Threading;
using System.Threading.Tasks;
using CinemaMode.Services;
using Shared;
using Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Shared.Models.AppConf;
using Shared.Models.Events;
using Shared.Models.Module;
using Shared.Models.Module.Interfaces;

namespace CinemaMode;

public class ModInit : IModuleLoaded
{
    public static string modpath = "";
    public static ModuleConf conf;

    static ILoggerFactory _loggers = NullLoggerFactory.Instance;
    static TrailerPoolManager? _pool;
    static CancellationTokenSource? _refreshCts;
    static Task? _refreshLoop;

    /// <summary>Singleton accessor used by the controller. May be null before Loaded().</summary>
    public static TrailerPoolManager? Pool => _pool;

    public void Loaded(InitspaceModel initspace)
    {
        modpath = initspace.path;

        var lf = initspace.app?.ApplicationServices?.GetService(typeof(ILoggerFactory)) as ILoggerFactory;
        if (lf != null) _loggers = lf;

        updateConf();
        EventListener.UpdateInitFile += updateConf;

        _pool = new TrailerPoolManager(
            _loggers.CreateLogger<TrailerPoolManager>(),
            new YtDlpRunner(_loggers.CreateLogger<YtDlpRunner>(), ResolveYtDlp(conf.ytdlp_path)));
        _pool.IsEnabled = conf.enabled;

        StartRefreshLoop();
    }

    public void Dispose()
    {
        EventListener.UpdateInitFile -= updateConf;
        StopRefreshLoop();

    }

    void updateConf()
    {
        conf = ModuleInvoke.Init("CinemaMode", new ModuleConf());
        if ((conf == null || string.IsNullOrWhiteSpace(conf.channel) || conf.pool_size <= 0)
            && CoreInit.CurrentConf != null
            && CoreInit.CurrentConf.TryGetValue("CinemaMode", out var raw))
        {
            conf = raw.ToObject<ModuleConf>() ?? new ModuleConf();
        }
        ReplaceYtDlpBinary();
    }

    static void ReplaceYtDlpBinary()
    {
        // Rebuild the runner whenever config changes so ytdlp_path edits take effect.
        _pool = new TrailerPoolManager(
            _loggers.CreateLogger<TrailerPoolManager>(),
            new YtDlpRunner(_loggers.CreateLogger<YtDlpRunner>(), ResolveYtDlp(conf.ytdlp_path)));
        if (conf != null) _pool.IsEnabled = conf.enabled;
    }


    static void StartRefreshLoop()
    {
        StopRefreshLoop();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        // Background refresh must do nothing when the operator has disabled the module.
        if (conf?.enabled == true && (conf?.refresh_on_start ?? true))
        {
            _ = Task.Run(() => RunRefreshOnce(token));
        }
        else if (conf?.enabled == false)
        {
            _loggers.CreateLogger("CinemaMode").LogInformation("CinemaMode: enabled=false, background refresh skipped");
        }
        _refreshLoop = Task.Run(() => Loop(token));
    }

    static void StopRefreshLoop()
    {
        try { _refreshCts?.Cancel(); } catch { }
        _refreshCts = null;
        _refreshLoop = null;
    }

    static async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var current = conf;
            var delay = TimeSpan.FromMinutes(Math.Max(1, current?.refresh_minutes ?? 360));
            try { await Task.Delay(delay, token); }
            catch (OperationCanceledException) { break; }
            await RunRefreshOnce(token);
        }
    }

    static async Task RunRefreshOnce(CancellationToken token)
    {
        try
        {
            var current = conf;
            if (current == null || _pool == null) return;
            // Honour the master switch at execution time as well; config reloads can flip enabled mid-loop.
            if (!current.enabled)
            {
                _pool.IsEnabled = false;
                return;
            }
            _pool.IsEnabled = true;
            var sources = current.EffectiveSources();
            if (sources.Count == 0) return;
            await _pool.RefreshAsync(sources, current.pool_size, current.max_height, current.storage_path, current.delete_old, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _loggers.CreateLogger("CinemaMode").LogWarning(ex, "CinemaMode: background refresh failed");
        }
    }

    static string ResolveYtDlp(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var candidate = Path.Combine(dir, "yt-dlp"); if (File.Exists(candidate)) return candidate; } catch { }
        }
        return "yt-dlp";
    }
}
