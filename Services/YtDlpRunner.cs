using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CinemaMode.Services;

/// <summary>Bounded, argument-list-only wrapper around yt-dlp.</summary>
public class YtDlpRunner
{
    static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);
    private readonly ILogger<YtDlpRunner> _log;
    private readonly string _binaryPath;

    public YtDlpRunner(ILogger<YtDlpRunner> log, string binaryPath)
    {
        _log = log;
        _binaryPath = binaryPath;
    }

    ProcessStartInfo NewStartInfo() => new()
    {
        FileName = _binaryPath,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    static async Task StopAndReapAsync(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
    }

    static async Task<(bool completed, bool timedOut)> WaitBoundedAsync(Process process, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return (true, false);
        }
        catch (OperationCanceledException)
        {
            await StopAndReapAsync(process).ConfigureAwait(false);
            return (false, timeout.IsCancellationRequested && !ct.IsCancellationRequested);
        }
    }

    public async Task<string> RunJsonAsync(string[] args, CancellationToken ct)
    {
        var psi = NewStartInfo();
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var wait = await WaitBoundedAsync(process, ct).ConfigureAwait(false);
            if (!wait.completed)
            {
                _log.LogWarning("CinemaMode: yt-dlp playlist {State}", wait.timedOut ? "timed out" : "cancelled");
                return "";
            }
            if (process.ExitCode != 0)
            {
                _log.LogWarning("CinemaMode: yt-dlp playlist exit {Code}: {Error}", process.ExitCode, stderr.ToString());
                return "";
            }
            return stdout.ToString();
        }
        catch (Exception ex)
        {
            await StopAndReapAsync(process).ConfigureAwait(false);
            _log.LogWarning(ex, "CinemaMode: yt-dlp playlist launch failed");
            return "";
        }
    }

    public async Task<YtDlpResult> DownloadAsync(string videoUrl, string outFile, int maxHeight, CancellationToken ct)
    {
        if (File.Exists(outFile)) return new YtDlpResult { ok = true, exit_code = 0, file_path = outFile };
        var directory = Path.GetDirectoryName(outFile)!;
        var id = Path.GetFileNameWithoutExtension(outFile);
        var temporaryTemplate = Path.Combine(directory, id + ".download.%(ext)s");
        var psi = NewStartInfo();
        psi.ArgumentList.Add("--no-progress"); psi.ArgumentList.Add("--no-warnings"); psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add($"bv*[height<={maxHeight}]+ba/b[height<={maxHeight}]/b");
        psi.ArgumentList.Add("--merge-output-format"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(temporaryTemplate);
        psi.ArgumentList.Add(videoUrl);
        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        try
        {
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            var wait = await WaitBoundedAsync(process, ct).ConfigureAwait(false);
            if (!wait.completed)
            {
                DeleteTemporary(directory, id);
                return new YtDlpResult { ok = false, exit_code = -1, stdout = stdout.ToString(), stderr = wait.timedOut ? "timeout" : "cancelled" };
            }
            if (process.ExitCode != 0)
            {
                DeleteTemporary(directory, id);
                return new YtDlpResult { ok = false, exit_code = process.ExitCode, stdout = stdout.ToString(), stderr = stderr.ToString() };
            }
            var produced = Directory.EnumerateFiles(directory, id + ".download.*")
                .FirstOrDefault(file => Path.GetExtension(file).Equals(".mp4", StringComparison.OrdinalIgnoreCase));
            if (produced == null || new FileInfo(produced).Length < 100_000)
            {
                DeleteTemporary(directory, id);
                return new YtDlpResult { ok = false, exit_code = process.ExitCode, stdout = stdout.ToString(), stderr = "no_playable_mp4" };
            }
            File.Move(produced, outFile, overwrite: false);
            DeleteTemporary(directory, id);
            return new YtDlpResult { ok = true, exit_code = 0, stdout = stdout.ToString(), stderr = stderr.ToString(), file_path = outFile };
        }
        catch (Exception ex)
        {
            await StopAndReapAsync(process).ConfigureAwait(false);
            DeleteTemporary(directory, id);
            _log.LogWarning(ex, "CinemaMode: yt-dlp download failed for {Url}", videoUrl);
            return new YtDlpResult { ok = false, exit_code = -1, stdout = stdout.ToString(), stderr = ex.Message };
        }
    }

    static void DeleteTemporary(string directory, string id)
    {
        try { foreach (var file in Directory.EnumerateFiles(directory, id + ".download.*")) File.Delete(file); } catch { }
    }
}

public class YtDlpResult
{
    public bool ok { get; set; }
    public int exit_code { get; set; }
    public string stdout { get; set; } = "";
    public string stderr { get; set; } = "";
    public string? file_path { get; set; }
}
