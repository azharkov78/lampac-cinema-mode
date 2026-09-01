namespace CinemaMode.Models;

/// <summary>
/// One trailer as stored on disk + exposed to the Lampa UI.
/// </summary>
public class TrailerRecord
{
    /// <summary>YouTube video ID (e.g. <c>dQw4w9WgXcQ</c>).</summary>
    public string id { get; set; } = "";

    /// <summary>Original video title from YouTube.</summary>
    public string title { get; set; } = "";

    /// <summary>Author/channel display name.</summary>
    public string author { get; set; } = "";

    /// <summary>ISO 8601 upload date (2026-08-31T19:00:00Z).</summary>
    public string upload_date { get; set; } = "";

    /// <summary>Duration in seconds.</summary>
    public int duration_seconds { get; set; }

    /// <summary>Relative path under wwwroot where the file is served from, e.g. <c>trailers/dQw4w9WgXcQ.mp4</c>.</summary>
    public string file_url { get; set; } = "";

    /// <summary>File size in bytes after download.</summary>
    public long size_bytes { get; set; }

    /// <summary>UTC timestamp when the local file finished downloading.</summary>
    public string downloaded_at { get; set; } = "";
}