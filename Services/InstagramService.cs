using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

public sealed class IgMediaResult
{
    public string? Caption { get; init; }
    public List<IgMediaItem> Items { get; init; } = [];
    public IgAudioInfo? Audio { get; init; }
    public string? Error { get; init; }

    public bool HasError => Error is not null;
    public bool HasAudio => Audio?.Url is not null;
    public bool HasImages => Items.Any(i => i.Type == "image");
    public bool HasVideos => Items.Any(i => i.Type == "video");
}

public sealed class IgMediaItem
{
    public string Type { get; init; } = "";
    public string Url { get; init; } = "";
    public string? Path { get; init; }
}

public sealed class IgAudioInfo
{
    public string? Url { get; init; }
    public string? Path { get; init; }
    public int StartMs { get; init; }
    public int DurationMs { get; init; }
}

/// <summary>
/// Instagram media extraction, no account/cookies required.
///
/// Strategy order (same approach the big "no login" downloader sites use):
///   1. Instagram's own anonymous GraphQL endpoint (doc_id query) — one POST
///      returns direct CDN URLs for posts, reels and carousels.
///   2. The public /embed/captioned/ page — works even when GraphQL is
///      rate-limited, since it's served by a different surface.
///   3. Public Cobalt instances — last resort only; their datacenter IPs are
///      usually blocked by Instagram, which is why they mostly fail.
///
/// Stories have no shortcode and always require an authenticated session, so
/// they can only ever succeed via Cobalt (rarely).
/// </summary>
public sealed partial class InstagramService
{
    // The web app id Instagram's own frontend sends. Public knowledge, not a secret.
    private const string IgAppId = "936619743392459";

    // PolarisPostActionLoadPostQuery — resolves a shortcode to full media JSON anonymously.
    private const string GraphQlDocId = "8845758582119845";

    private readonly HttpClient _http;
    private readonly ILogger<InstagramService> _log;

    private string[] _cobaltInstances = Array.Empty<string>();
    private DateTime _instancesLastFetched = DateTime.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public InstagramService(ILogger<InstagramService> log)
    {
        _log = log;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };

        // 10 second timeout: we want to fail FAST on bad instances
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        _http.DefaultRequestHeaders.Add("Accept", "*/*");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
    }

    public bool IsAvailable => true;

    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        var shortcode = ExtractShortcode(url);

        if (shortcode is not null)
        {
            var result = await TryGraphQlAsync(shortcode, ct);
            if (result is { Items.Count: > 0 })
            {
                _log.LogInformation("Extracted {N} items via Instagram GraphQL", result.Items.Count);
                return result;
            }

            result = await TryEmbedPageAsync(shortcode, ct);
            if (result is { Items.Count: > 0 })
            {
                _log.LogInformation("Extracted {N} items via Instagram embed page", result.Items.Count);
                return result;
            }

            _log.LogWarning("Direct Instagram extraction failed for {Shortcode}, falling back to Cobalt", shortcode);
        }

        return await TryCobaltAsync(url, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Strategy 1: anonymous GraphQL (posts / reels / carousels)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<IgMediaResult?> TryGraphQlAsync(string shortcode, CancellationToken ct)
    {
        try
        {
            var variables = JsonSerializer.Serialize(new
            {
                shortcode,
                fetch_tagged_user_count = (object?)null,
                hoisted_comment_id = (object?)null,
                hoisted_reply_id = (object?)null,
            });

            var request = new HttpRequestMessage(HttpMethod.Post, "https://www.instagram.com/graphql/query")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["av"] = "0",
                    ["__d"] = "www",
                    ["lsd"] = "AVqbxe3J_YA",
                    ["variables"] = variables,
                    ["server_timestamps"] = "true",
                    ["doc_id"] = GraphQlDocId,
                }),
            };
            request.Headers.Add("X-IG-App-ID", IgAppId);
            request.Headers.Add("X-FB-LSD", "AVqbxe3J_YA");
            request.Headers.Add("X-ASBD-ID", "129477");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            request.Headers.Referrer = new Uri($"https://www.instagram.com/p/{shortcode}/");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Instagram GraphQL returned HTTP {Code}", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("xdt_shortcode_media", out var media) ||
                media.ValueKind != JsonValueKind.Object)
            {
                _log.LogWarning("Instagram GraphQL: no media in response (private/removed post, or rate limited)");
                return null;
            }

            var items = new List<IgMediaItem>();

            if (media.TryGetProperty("edge_sidecar_to_children", out var sidecar) &&
                sidecar.TryGetProperty("edges", out var edges))
            {
                foreach (var edge in edges.EnumerateArray())
                    if (edge.TryGetProperty("node", out var node))
                        AddGraphQlNode(node, items);
            }
            else
            {
                AddGraphQlNode(media, items);
            }

            string? caption = null;
            if (media.TryGetProperty("edge_media_to_caption", out var capEdges) &&
                capEdges.TryGetProperty("edges", out var capArr) &&
                capArr.GetArrayLength() > 0 &&
                capArr[0].TryGetProperty("node", out var capNode) &&
                capNode.TryGetProperty("text", out var capText))
            {
                caption = capText.GetString();
            }

            return items.Count > 0 ? new IgMediaResult { Items = items, Caption = caption } : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Instagram GraphQL timed out");
            return null;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Instagram GraphQL failed: {Msg}", ex.Message);
            return null;
        }
    }

    private static void AddGraphQlNode(JsonElement node, List<IgMediaItem> items)
    {
        var isVideo = node.TryGetProperty("is_video", out var iv) && iv.GetBoolean();

        if (isVideo && node.TryGetProperty("video_url", out var vu) && vu.GetString() is { Length: > 0 } videoUrl)
        {
            items.Add(new IgMediaItem { Type = "video", Url = videoUrl });
        }
        else if (node.TryGetProperty("display_url", out var du) && du.GetString() is { Length: > 0 } displayUrl)
        {
            items.Add(new IgMediaItem { Type = "image", Url = displayUrl });
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Strategy 2: public embed page (different surface, separate rate limits)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<IgMediaResult?> TryEmbedPageAsync(string shortcode, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.instagram.com/p/{shortcode}/embed/captioned/");
            request.Headers.Referrer = new Uri("https://www.instagram.com/");

            using var response = await _http.SendAsync(request, ct);
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

            return items.Count > 0 ? new IgMediaResult { Items = items } : null;
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

    // ═══════════════════════════════════════════════════════════════════
    // Strategy 3: public Cobalt instances (last resort)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<string[]> GetInstancesAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // Cache for 1 hour to keep the bot lightning fast
            if (DateTime.UtcNow - _instancesLastFetched < TimeSpan.FromHours(1) && _cobaltInstances.Length > 0)
                return _cobaltInstances;

            _log.LogInformation("Fetching 'working' Cobalt instances for Instagram from cobalt.directory...");

            var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://cobalt.directory/api/working?type=api");
                using var response = await _http.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    // Navigate straight to data -> instagram
                    if (doc.RootElement.TryGetProperty("data", out var dataProp) &&
                        dataProp.TryGetProperty("instagram", out var igInstances))
                    {
                        foreach (var item in igInstances.EnumerateArray())
                        {
                            var url = item.GetString();
                            if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
                            {
                                list.Add(url);
                            }
                        }
                    }
                }
                else
                {
                    _log.LogWarning("Directory API returned HTTP {Code}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("Failed to fetch from JSON API: {Msg}", ex.Message);
            }

            // Always inject highly reliable known instances at the end of the queue
            var rockSolidFallbacks = new[]
            {
                "https://api.cobalt.blackcat.sweeux.org",
                "https://cobalt.canine.tools",
                "https://api.cobalt.tools" // The official API
            };

            foreach (var fallback in rockSolidFallbacks)
            {
                list.Add(fallback);
            }

            // Clean up: trailing slashes, remove official from top to prevent AWS bans, limit size
            _cobaltInstances = list
                .Select(url => url.TrimEnd('/'))
                .Distinct()
                .Where(url => !url.Contains("api.cobalt.tools"))
                .Append("https://api.cobalt.tools")
                .Take(20)
                .ToArray();

            _instancesLastFetched = DateTime.UtcNow;
            _log.LogInformation("Successfully extracted and cached {Count} Cobalt URLs via JSON.", _cobaltInstances.Length);

            return _cobaltInstances;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<IgMediaResult> TryCobaltAsync(string url, CancellationToken ct)
    {
        var payload = new { url = url };
        var instances = await GetInstancesAsync(ct);

        foreach (var instance in instances)
        {
            try
            {
                var requestUrl = instance.EndsWith("/") ? instance : instance + "/";

                // Add the browser headers right before sending the request to bypass WAFs
                var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
                request.Headers.Add("Origin", "https://cobalt.tools");
                request.Headers.Add("Referer", "https://cobalt.tools/");
                request.Content = JsonContent.Create(payload);

                var response = await _http.SendAsync(request, ct);

                if (!response.IsSuccessStatusCode)
                    continue; // Skip silently to burn through bad URLs fast

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var status = root.GetProperty("status").GetString();
                var items = new List<IgMediaItem>();

                if (status == "error")
                {
                    var errObj = root.GetProperty("error");
                    var code = errObj.TryGetProperty("code", out var c) ? c.GetString() : "Unknown";
                    _log.LogWarning("Cobalt {Instance} returned API error: {Code}", instance, code);
                    continue;
                }

                if (status is "tunnel" or "redirect")
                {
                    var mediaUrl = root.GetProperty("url").GetString();
                    var type = (mediaUrl?.Contains(".jpg") == true || mediaUrl?.Contains(".webp") == true) ? "image" : "video";

                    if (!string.IsNullOrEmpty(mediaUrl))
                        items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
                }
                else if (status == "picker")
                {
                    var picker = root.GetProperty("picker").EnumerateArray();
                    foreach (var item in picker)
                    {
                        var type = item.GetProperty("type").GetString() == "photo" ? "image" : "video";
                        var mediaUrl = item.GetProperty("url").GetString();

                        if (!string.IsNullOrEmpty(mediaUrl))
                            items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
                    }
                }

                if (items.Count > 0)
                {
                    _log.LogInformation("Successfully extracted {N} items via {Instance}", items.Count, instance);
                    return new IgMediaResult { Items = items, Audio = null, Caption = null };
                }
            }
            catch (TaskCanceledException)
            {
                // Timeout - move on
            }
            catch (Exception)
            {
                // Network error or bad JSON format. Just skip!
            }
        }

        return new IgMediaResult { Error = "Instagram blocked all extraction methods (direct API, embed page, and Cobalt instances). Try again later." };
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string? ExtractShortcode(string url)
    {
        var match = ShortcodeRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"instagram\.com/(?:[^/]+/)?(?:p|reels?|tv)/([A-Za-z0-9_-]{5,})", RegexOptions.IgnoreCase)]
    private static partial Regex ShortcodeRegex();

    [GeneratedRegex("\"video_url\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)+)\"")]
    private static partial Regex EmbedVideoUrlRegex();

    [GeneratedRegex("class=\"EmbeddedMediaImage\"[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedImageRegex();
}
