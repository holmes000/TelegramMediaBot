using System.Net;
using System.Text.RegularExpressions;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Tier 3: InstaFix-style embed-fixer services (ddinstagram and friends).
/// These community-run resolvers exist so Telegram/Discord can embed Instagram
/// links; they serve OG meta tags with direct (or proxied) media URLs to bot
/// user agents. Single item only — fixers don't expose full carousels — but
/// videos are the pain point. Hosts are configurable since fixers come and go.
/// </summary>
public sealed partial class EmbedFixerStrategy : IIgStrategy
{
    private const string BotUserAgent = "TelegramBot (like TwitterBot)";

    private readonly HttpClient _http;
    private readonly BotConfig _cfg;
    private readonly ILogger _log;

    public EmbedFixerStrategy(HttpClient http, BotConfig cfg, ILogger log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Name => "Embed fixers";

    public async Task<IgMediaResult?> TryFetchAsync(IgRequest request, CancellationToken ct)
    {
        if (request.Shortcode is not { } shortcode) return null;

        // Reels must yield a video; an image-only OG result is just the thumbnail.
        var requireVideo = request.Url.Contains("/reel", StringComparison.OrdinalIgnoreCase) ||
                           request.Url.Contains("/tv/", StringComparison.OrdinalIgnoreCase);

        foreach (var host in _cfg.EmbedFixerHosts)
        {
            try
            {
                var baseUri = new Uri($"https://{host.Trim().TrimEnd('/')}/");
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"p/{shortcode}/"));
                // Fixers only serve OG meta to crawler user agents; browsers get redirected to instagram.com.
                httpRequest.Headers.TryAddWithoutValidation("User-Agent", BotUserAgent);

                using var response = await _http.SendAsync(httpRequest, ct);
                if (!response.IsSuccessStatusCode) continue;

                var html = await response.Content.ReadAsStringAsync(ct);
                var item = ParseOgMedia(html, baseUri, requireVideo);
                if (item is null) continue;

                _log.LogInformation("Embed fixer {Host} resolved {Shortcode}", host, shortcode);
                return new IgMediaResult { Items = [item] };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout — try the next host
            }
            catch (Exception ex)
            {
                _log.LogWarning("Embed fixer {Host} failed: {Msg}", host, ex.Message);
            }
        }

        return null;
    }

    /// <summary>Extracts the best media item from a fixer page's OG meta tags.</summary>
    public static IgMediaItem? ParseOgMedia(string html, Uri baseUri, bool requireVideo)
    {
        var video = MatchOgContent(html, "og:video") ?? MatchOgContent(html, "og:video:secure_url");
        if (video is not null && Resolve(video, baseUri) is { } videoUrl)
            return new IgMediaItem { Type = "video", Url = videoUrl };

        if (requireVideo) return null;

        var image = MatchOgContent(html, "og:image");
        if (image is not null && Resolve(image, baseUri) is { } imageUrl)
            return new IgMediaItem { Type = "image", Url = imageUrl };

        return null;
    }

    private static string? MatchOgContent(string html, string property)
    {
        // <meta property="og:video" content="..."/> — attribute order varies between fixers.
        var prop = Regex.Escape(property);
        var pattern =
            $"<meta[^>]+(?:property|name)=\"{prop}\"[^>]+content=\"([^\"]+)\"" +
            $"|<meta[^>]+content=\"([^\"]+)\"[^>]+(?:property|name)=\"{prop}\"";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        var decoded = WebUtility.HtmlDecode(raw);
        return string.IsNullOrWhiteSpace(decoded) ? null : decoded;
    }

    private static string? Resolve(string url, Uri baseUri) =>
        Uri.TryCreate(baseUri, url, out var abs) && abs.Scheme is "http" or "https"
            ? abs.AbsoluteUri
            : null;
}
