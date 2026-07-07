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

        // Instagram alternates between serving the real embed (with the full
        // contextJSON) and a login wall on a per-request basis, so a single
        // cheap retry makes this tier far more consistent.
        for (var attempt = 1; attempt <= 2; attempt++)
        {
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

                // Best case: the page embeds the full GraphQL data (contextJSON) —
                // every carousel child and real video URLs, no guessing.
                var (jsonItems, caption) = ParseContextJson(html);
                if (jsonItems.Count > 0)
                {
                    _log.LogInformation("Instagram embed page contextJSON resolved {N} item/s", jsonItems.Count);
                    return new IgMediaResult { Items = jsonItems, Caption = caption, Authoritative = true };
                }

                var (item, authoritative) = ParseEmbedMedia(html, request.RequireVideo);
                if (item is not null)
                    return new IgMediaResult { Items = [item], Authoritative = authoritative };

                if (attempt == 1)
                {
                    _log.LogInformation("Instagram embed page had no usable media (likely login wall) — retrying once");
                    await Task.Delay(300, ct);
                    continue;
                }

                _log.LogWarning("Instagram embed page returned HTTP 200 but no usable media " +
                    "(login/challenge wall, or a video post whose embed only exposes the thumbnail)");
                return null;
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

        return null;
    }

    /// <summary>
    /// Extracts the full media set from the contextJSON blob embedded in the
    /// page's scripts (the same shortcode_media JSON the GraphQL API returns,
    /// double-escaped inside a string literal). Present on most embeds; when
    /// Instagram serves a login wall it's absent and we fall back to markup.
    /// </summary>
    public static (List<IgMediaItem> Items, string? Caption) ParseContextJson(string html)
    {
        try
        {
            var match = ContextJsonRegex().Match(html);
            if (!match.Success) return ([], null);

            var json = JsonSerializer.Deserialize<string>($"\"{match.Groups[1].Value}\"");
            if (string.IsNullOrEmpty(json)) return ([], null);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement media = default;
            var found =
                (root.TryGetProperty("gql_data", out var gql) &&
                 gql.ValueKind == JsonValueKind.Object &&
                 gql.TryGetProperty("shortcode_media", out media)) ||
                (root.TryGetProperty("graphql", out var graphql) &&
                 graphql.ValueKind == JsonValueKind.Object &&
                 graphql.TryGetProperty("shortcode_media", out media)) ||
                root.TryGetProperty("shortcode_media", out media);

            if (!found || media.ValueKind != JsonValueKind.Object) return ([], null);

            return IgGraphJson.ParseShortcodeMedia(media);
        }
        catch
        {
            return ([], null);
        }
    }

    /// <summary>
    /// Extracts the media item from embed-page HTML.
    ///
    /// The EmbeddedMediaImage tag means different things depending on the
    /// post's data-media-type marker:
    ///   GraphVideo   → it's the poster thumbnail; never deliver it.
    ///   GraphImage   → it's the actual (single) image; authoritative — this
    ///                  also covers photo posts shared via /reel/ links, since
    ///                  Instagram mixes photos into the reels feed.
    ///   GraphSidecar → it's only the first image of a carousel; usable as a
    ///                  last resort, but a tier that can enumerate the full
    ///                  album (Cobalt/GraphQL) should be preferred.
    ///   no marker    → can't trust it for a video-hinted URL.
    /// </summary>
    public static (IgMediaItem? Item, bool Authoritative) ParseEmbedMedia(string html, bool requireVideo)
    {
        // Video URL lives inside embedded JSON, escaped (\" \/ &).
        var videoMatch = EmbedVideoUrlRegex().Match(html);
        if (videoMatch.Success &&
            JsonSerializer.Deserialize<string>($"\"{videoMatch.Groups[1].Value}\"") is { Length: > 0 } videoUrl)
        {
            return (new IgMediaItem { Type = "video", Url = videoUrl }, true);
        }

        var mediaType = MediaTypeRegex().Match(html) is { Success: true } m ? m.Groups[1].Value : "";
        var declaresVideo = mediaType.Contains("Video", StringComparison.OrdinalIgnoreCase) ||
                            html.Contains("\"is_video\":true", StringComparison.OrdinalIgnoreCase);

        if (declaresVideo) return (null, false);                 // video post, only the poster exposed
        if (requireVideo && mediaType.Length == 0) return (null, false); // unmarked page for a reel URL — thumbnail risk

        var imgMatch = EmbedImageRegex().Match(html);
        if (!imgMatch.Success) return (null, false);

        var item = new IgMediaItem { Type = "image", Url = WebUtility.HtmlDecode(imgMatch.Groups[1].Value) };
        var isCompleteSingleImage = mediaType.Contains("GraphImage", StringComparison.OrdinalIgnoreCase);
        return (item, isCompleteSingleImage);
    }

    [GeneratedRegex("\"video_url\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)+)\"")]
    private static partial Regex EmbedVideoUrlRegex();

    [GeneratedRegex("class=\"EmbeddedMediaImage\"[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedImageRegex();

    [GeneratedRegex("data-media-type=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex MediaTypeRegex();

    [GeneratedRegex("\"contextJSON\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex ContextJsonRegex();
}
