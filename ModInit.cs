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
    }


    static void StartRefreshLoop()
    {
        StopRefreshLoop();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        if (conf?.refresh_on_start ?? true)
        {
            _ = Task.Run(() => RunRefreshOnce(token));
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
            await _pool.RefreshAsync(current.channel, current.pool_size, current.max_height, current.storage_path, token);
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
