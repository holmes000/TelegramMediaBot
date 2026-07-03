using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Tier 2: the public /embed/captioned/ page. Served by a different Instagram
/// surface with separate rate limits, so it often works when GraphQL is
/// blocked. Single item only (the embed page doesn't expose full carousels).
/// </summary>
public sealed partial class EmbedPageStrategy : IIgStrategy
{
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public EmbedPageStrategy(HttpClient http, ILogger log)
    {
        _http = http;
        _log = log;
    }

    public string Name => "Instagram embed page";

    public async Task<IgMediaResult?> TryFetchAsync(IgRequest request, CancellationToken ct)
    {
        if (request.Shortcode is not { } shortcode) return null;

        try
        {
            var httpRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.instagram.com/p/{shortcode}/embed/captioned/");
            httpRequest.Headers.Referrer = new Uri("https://www.instagram.com/");

            using var response = await _http.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Instagram embed page returned HTTP {Code}", (int)response.StatusCode);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var item = ParseEmbedMedia(html, request.RequireVideo);

            if (item is null)
            {
                _log.LogWarning("Instagram embed page returned HTTP 200 but no usable media " +
                    "(login/challenge wall, or a video post whose embed only exposes the thumbnail)");
                return null;
            }

            return new IgMediaResult { Items = [item] };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Instagram embed page timed out");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Instagram embed page failed: {Msg}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Extracts the media item from embed-page HTML. The EmbeddedMediaImage tag
    /// is the *poster thumbnail* when the post is a video, so it is only
    /// accepted when nothing marks the post as video — otherwise a reel would
    /// be delivered as a JPG.
    /// </summary>
    public static IgMediaItem? ParseEmbedMedia(string html, bool requireVideo)
    {
        // Video URL lives inside embedded JSON, escaped (\" \/ &).
        var videoMatch = EmbedVideoUrlRegex().Match(html);
        if (videoMatch.Success &&
            JsonSerializer.Deserialize<string>($"\"{videoMatch.Groups[1].Value}\"") is { Length: > 0 } videoUrl)
        {
            return new IgMediaItem { Type = "video", Url = videoUrl };
        }

        if (requireVideo || DeclaresVideo(html)) return null;

        // Image posts render an <img class="EmbeddedMediaImage"> tag with HTML-escaped src.
        var imgMatch = EmbedImageRegex().Match(html);
        return imgMatch.Success
            ? new IgMediaItem { Type = "image", Url = WebUtility.HtmlDecode(imgMatch.Groups[1].Value) }
            : null;
    }

    private static bool DeclaresVideo(string html) =>
        (MediaTypeRegex().Match(html) is { Success: true } m &&
         m.Groups[1].Value.Contains("Video", StringComparison.OrdinalIgnoreCase)) ||
        html.Contains("\"is_video\":true", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("\"video_url\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)+)\"")]
    private static partial Regex EmbedVideoUrlRegex();

    [GeneratedRegex("class=\"EmbeddedMediaImage\"[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedImageRegex();

    [GeneratedRegex("data-media-type=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MediaTypeRegex();
}
