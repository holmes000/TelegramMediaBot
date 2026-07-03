using System.Text.RegularExpressions;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>A single Instagram extraction request: the original URL plus its shortcode (null for stories).</summary>
public sealed record IgRequest(string Url, string? Shortcode);

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

    [GeneratedRegex(@"instagram\.com/(?:[^/]+/)?(?:p|reels?|tv)/([A-Za-z0-9_-]{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex ShortcodeRegex();
}
