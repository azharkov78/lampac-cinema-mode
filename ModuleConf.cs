using System.Collections.Generic;
using Shared.Models.Module;

namespace CinemaMode;

/// <summary>User-facing settings for the Cinema Mode module.</summary>
public class ModuleConf : ModuleBaseConf
{
    /// <summary>Master switch. When false the backend never downloads and random returns empty.</summary>
    public bool enabled { get; set; } = true;

    /// <summary>YouTube channel URLs or @handles to pull trailers from (newest first per source).</summary>
    public List<string> sources { get; set; } = new();

    /// <summary>Legacy single-channel field. Used as a source if sources is empty after deserialization.</summary>
    public string channel { get; set; } = "@KinomanTrailers";

    /// <summary>Maximum number of CinemaMode-owned trailers retained on disk. Hard-clamped 1..10.</summary>
    public int pool_size { get; set; } = 10;

    /// <summary>How many trailers to play per movie / per manual start.</summary>
    public int trailers_per_movie { get; set; } = 3;

    /// <summary>When true, retention evicts oldest owned files to honour pool_size.</summary>
    public bool delete_old { get; set; } = true;

    public string storage_path { get; set; } = "/opt/lampac/wwwroot/trailers";
    public string ytdlp_path { get; set; } = "/usr/local/bin/yt-dlp";
    public int max_height { get; set; } = 1080;
    public int refresh_minutes { get; set; } = 360;
    public bool refresh_on_start { get; set; } = true;

    /// <summary>Effective list of sources, falling back to legacy <c>channel</c> when sources is empty.</summary>
    public IReadOnlyList<string> EffectiveSources()
    {
        if (sources != null && sources.Count > 0)
        {
            var cleaned = new List<string>(sources.Count);
            foreach (var s in sources) if (!string.IsNullOrWhiteSpace(s)) cleaned.Add(s.Trim());
            if (cleaned.Count > 0) return cleaned;
        }
        return string.IsNullOrWhiteSpace(channel) ? new List<string>() : new List<string> { channel.Trim() };
    }
}
