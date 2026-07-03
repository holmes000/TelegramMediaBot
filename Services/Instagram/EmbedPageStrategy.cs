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
            var items = new List<IgMediaItem>();

            // Video URL lives inside embedded JSON, escaped (\" \/ &).
            var videoMatch = EmbedVideoUrlRegex().Match(html);
            if (videoMatch.Success &&
                JsonSerializer.Deserialize<string>($"\"{videoMatch.Groups[1].Value}\"") is { Length: > 0 } videoUrl)
            {
                items.Add(new IgMediaItem { Type = "video", Url = videoUrl });
            }
            else
            {
                // Image posts render an <img class="EmbeddedMediaImage"> tag with HTML-escaped src.
                var imgMatch = EmbedImageRegex().Match(html);
                if (imgMatch.Success)
                    items.Add(new IgMediaItem { Type = "image", Url = WebUtility.HtmlDecode(imgMatch.Groups[1].Value) });
            }

            if (items.Count == 0)
            {
                _log.LogWarning("Instagram embed page returned HTTP 200 but no media markup (likely a login/challenge wall)");
                return null;
            }

            return new IgMediaResult { Items = items };
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

    [GeneratedRegex("\"video_url\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)+)\"")]
    private static partial Regex EmbedVideoUrlRegex();

    [GeneratedRegex("class=\"EmbeddedMediaImage\"[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedImageRegex();
}
