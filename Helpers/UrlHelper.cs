using System.Text.RegularExpressions;

namespace TelegramMediaBot.Helpers;

public static partial class UrlHelper
{
    private static readonly string[] SupportedDomains =
        ["tiktok.com", "vm.tiktok.com", "vt.tiktok.com", "instagram.com", "www.instagram.com", "ddinstagram.com", "www.tiktok.com"];

    public static List<string> ExtractUrls(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return [.. UrlRegex().Matches(text)
            .Cast<Match>()
            .Select(m => m.Value)
            .Where(IsSupportedUrl)];
    }

    public static bool IsSupportedUrl(string url) =>
        SupportedDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase)) &&
        IsDirectContentUrl(url);

    /// <summary>
    /// Only accept URLs that point to specific content (post, reel, video, story).
    /// Reject profile pages, home pages, explore pages, hashtag pages, etc.
    /// </summary>
    public static bool IsDirectContentUrl(string url)
    {
        // Instagram: must contain /p/, /reel/, /stories/, /tv/
        if (IsInstagramUrl(url))
        {
            return url.Contains("/p/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/reel/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/reels/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/stories/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/tv/", StringComparison.OrdinalIgnoreCase);
        }

        // TikTok: must contain /video/, /photo/, or be a vm.tiktok.com short link
        if (url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            return url.Contains("/video/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("/photo/", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("vm.tiktok.com", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public static bool IsInstagramUrl(string url) =>
        url.Contains("instagram.com", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("ddinstagram.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// URLs known to be photo/slideshow that yt-dlp can't handle.
    /// Detected before calling yt-dlp to avoid the slow "Unsupported URL" error.
    /// </summary>
    public static bool IsLikelyPhotoUrl(string url) =>
        url.Contains("/photo/", StringComparison.OrdinalIgnoreCase) &&
        url.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.Compiled)]
    private static partial Regex UrlRegex();
}

public static class FileTypeHelper
{
    private static readonly HashSet<string> VideoExts = [".mp4", ".webm", ".mkv", ".avi", ".mov", ".flv"];
    private static readonly HashSet<string> ImageExts = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];
    private static readonly HashSet<string> AudioExts = [".mp3", ".m4a", ".ogg", ".opus", ".aac", ".wav"];

    public static bool IsVideo(string path)  => VideoExts.Contains(Ext(path));
    public static bool IsImage(string path)  => ImageExts.Contains(Ext(path));
    public static bool IsAudio(string path)  => AudioExts.Contains(Ext(path));
    public static bool IsMedia(string path)  => IsVideo(path) || IsImage(path);

    public static string Classify(string ext)
    {
        ext = ext.TrimStart('.').ToLowerInvariant();
        if (VideoExts.Contains($".{ext}")) return "video";
        if (ImageExts.Contains($".{ext}")) return "image";
        if (AudioExts.Contains($".{ext}")) return "audio";
        return "unknown";
    }

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();
}
