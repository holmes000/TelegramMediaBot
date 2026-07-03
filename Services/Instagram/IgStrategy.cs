using System.Text.RegularExpressions;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// A single Instagram extraction request: the original URL, its shortcode
/// (null for stories), and whether the URL guarantees video content (reels/tv)
/// — in which case image-only results are thumbnails and must be rejected.
/// </summary>
public sealed record IgRequest(string Url, string? Shortcode, bool RequireVideo);

/// <summary>
/// One tier of the Instagram extraction chain. Returns null (or a result with
/// no items) when this tier can't fetch the media — the orchestrator moves on
/// to the next tier. Implementations must not throw for expected failures.
/// </summary>
public interface IIgStrategy
{
    string Name { get; }
    Task<IgMediaResult?> TryFetchAsync(IgRequest request, CancellationToken ct);
}

public static partial class IgUrl
{
    public static string? ExtractShortcode(string url)
    {
        var match = ShortcodeRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Reels and IGTV are always video; /p/ posts can be either.</summary>
    public static bool IsVideoUrl(string url) =>
        url.Contains("/reel", StringComparison.OrdinalIgnoreCase) ||
        url.Contains("/tv/", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"instagram\.com/(?:[^/]+/)?(?:p|reels?|tv)/([A-Za-z0-9_-]{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex ShortcodeRegex();
}
