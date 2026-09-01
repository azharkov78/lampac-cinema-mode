using Shared.Models.Module;

namespace CinemaMode;

/// <summary>User-facing settings for the Cinema Mode module.</summary>
public class ModuleConf : ModuleBaseConf
{
    public string channel { get; set; } = "@KinomanTrailers";
    public int pool_size { get; set; } = 10;
    public int trailers_per_movie { get; set; } = 3;
    public string storage_path { get; set; } = "/opt/lampac/wwwroot/trailers";
    public string ytdlp_path { get; set; } = "/usr/local/bin/yt-dlp";
    public int max_height { get; set; } = 1080;
    public int refresh_minutes { get; set; } = 360;
    public bool refresh_on_start { get; set; } = true;
}
