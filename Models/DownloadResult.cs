namespace TelegramMediaBot.Models;

/// <summary>
/// Outcome of processing a single media URL. Exactly one of the delivery
/// properties will be set on a successful result.
/// </summary>
public sealed class DownloadResult : IDisposable
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Caption { get; init; }

    // ── Delivery options (mutually exclusive, checked in priority order) ──

    /// <summary>Piped stdout stream from yt-dlp (video, no disk).</summary>
    public Stream? VideoStream { get; init; }
    public System.Diagnostics.Process? StreamProcess { get; init; }

    /// <summary>Direct CDN URLs — Telegram fetches server-side (no disk).</summary>
    public List<(string Url, string Category)>? MediaUrls { get; init; }

    /// <summary>Mixed album of local file paths (videos + images).</summary>
    public List<string>? AlbumPaths { get; init; }

    /// <summary>Single video file on disk.</summary>
    public string? VideoPath { get; init; }

    /// <summary>Photo file paths on disk.</summary>
    public List<string>? ImagePaths { get; init; }

    // ── Helpers ───────────────────────────────────────────────────────

    public bool IsStreamed => VideoStream is not null;

    public static DownloadResult Fail(string error) => new() { Success = false, Error = error };

    public void Dispose()
    {
        try { StreamProcess?.Kill(entireProcessTree: true); } catch { }
        StreamProcess?.Dispose();
        VideoStream?.Dispose();
    }
}

/// <summary>
/// A media item extracted from gallery-dl --dump-json (CDN URL, no local file).
/// </summary>
public sealed record GalleryDlMediaItem(string Url, string Extension, string Category);
