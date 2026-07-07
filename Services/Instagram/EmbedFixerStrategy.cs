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

        foreach (var host in _cfg.EmbedFixerHosts)
        {
            try
            {
                var baseUri = new Uri($"https://{host.Trim().TrimEnd('/')}/");
                var httpRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"p/{shortcode}/"));
                // Fixers only serve OG meta to crawler user agents; browsers get redirected to instagram.com.
                httpRequest.Headers.TryAddWithoutValidation("User-Agent", BotUserAgent);

                using var response = await _http.SendAsync(httpRequest, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Embed fixer {Host} returned HTTP {Code}", host, (int)response.StatusCode);
                    continue;
                }

                var html = await response.Content.ReadAsStringAsync(ct);
                var item = ParseOgMedia(html, baseUri, request.RequireVideo);
                if (item is null)
                {
                    _log.LogWarning("Embed fixer {Host} returned no usable OG media for {Shortcode}", host, shortcode);
                    continue;
                }

                // Some fixers put the thumbnail URL in og:video when their own
                // extraction only got the poster — verify the bytes are video.
                if (item.Type == "video" && !await ServesVideoBytesAsync(item.Url, ct))
                {
                    _log.LogWarning("Embed fixer {Host} og:video for {Shortcode} does not serve video content ({Url}) — trying next host",
                        host, shortcode, item.Url);
                    continue;
                }

                // og:image is a cropped preview and covers only the first item.
                // InstaFix-style hosts expose /images/{code}/{n} redirecting to
                // the full-res originals — enumerate them for the real album.
                var items = new List<IgMediaItem> { item };
                if (item.Type == "image")
                {
                    var album = await EnumerateAlbumImagesAsync(baseUri, shortcode, ct);
                    if (album.Count > 0) items = album;
                }

                _log.LogInformation("Embed fixer {Host} resolved {Shortcode} → {N} item/s ({Type})", host, shortcode, items.Count, item.Type);
                return new IgMediaResult { Items = items };
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

    /// <summary>
    /// Extracts the best media item from a fixer page's OG meta tags. When the
    /// page self-identifies as a video but exposes no og:video URL, the
    /// og:image is just the thumbnail — reject it so the next tier can deliver
    /// the actual video.
    /// </summary>
    public static IgMediaItem? ParseOgMedia(string html, Uri baseUri, bool requireVideo)
    {
        var video = MatchOgContent(html, "og:video") ?? MatchOgContent(html, "og:video:secure_url");
        if (video is not null && Resolve(video, baseUri) is { } videoUrl)
            return new IgMediaItem { Type = "video", Url = videoUrl };

        if (requireVideo || DeclaresVideo(html)) return null;

        var image = MatchOgContent(html, "og:image");
        if (image is not null && Resolve(image, baseUri) is { } imageUrl)
            return new IgMediaItem { Type = "image", Url = imageUrl };

        return null;
    }

    private const int MaxAlbumImages = 10;

    /// <summary>
    /// Walks the fixer's /images/{shortcode}/{n} endpoints, which redirect to
    /// the full-resolution originals, until one is missing. Returns the final
    /// (post-redirect) CDN URLs. Empty when the host doesn't support the
    /// endpoint — the caller keeps the og:image preview in that case.
    /// </summary>
    private async Task<List<IgMediaItem>> EnumerateAlbumImagesAsync(Uri baseUri, string shortcode, CancellationToken ct)
    {
        var items = new List<IgMediaItem>();

        for (var n = 1; n <= MaxAlbumImages; n++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, $"images/{shortcode}/{n}"));
                request.Headers.TryAddWithoutValidation("User-Agent", BotUserAgent);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) break;

                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) break;

                var finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri;
                if (string.IsNullOrEmpty(finalUrl)) break;

                items.Add(new IgMediaItem { Type = "image", Url = finalUrl });
            }
            catch
            {
                break;
            }
        }

        return items;
    }

    /// <summary>
    /// Fetches the first bytes of a claimed video URL and checks it actually
    /// serves video. Trusts a video/* Content-Type, rejects image/*, and
    /// sniffs file magic when the type is ambiguous (octet-stream etc.).
    /// </summary>
    private async Task<bool> ServesVideoBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BotUserAgent);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4095);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return false;

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return true;
            if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return false;

            var buffer = new byte[16];
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);
            return SniffIsVideo(buffer.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>File-magic sniff: true for MP4/MOV/WebM, false for known image formats, true when unknown.</summary>
    public static bool SniffIsVideo(ReadOnlySpan<byte> header)
    {
        // MP4/MOV: "ftyp" at offset 4
        if (header.Length >= 8 && header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p') return true;
        // WebM/MKV: EBML magic
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3) return true;
        // JPEG
        if (header.Length >= 2 && header[0] == 0xFF && header[1] == 0xD8) return false;
        // PNG
        if (header.Length >= 4 && header[0] == 0x89 && header[1] == 'P' && header[2] == 'N' && header[3] == 'G') return false;
        // GIF
        if (header.Length >= 3 && header[0] == 'G' && header[1] == 'I' && header[2] == 'F') return false;
        // WEBP: RIFF....WEBP
        if (header.Length >= 12 && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P') return false;

        // Unknown container — don't over-reject
        return true;
    }

    private static bool DeclaresVideo(string html) =>
        MatchOgContent(html, "og:type")?.Contains("video", StringComparison.OrdinalIgnoreCase) == true ||
        string.Equals(MatchOgContent(html, "twitter:card"), "player", StringComparison.OrdinalIgnoreCase);

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
