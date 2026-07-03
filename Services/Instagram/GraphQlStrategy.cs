using System.Text.Json;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Tier 1: Instagram's own anonymous GraphQL endpoint — the same doc_id query
/// the logged-out web app sends. One POST returns direct CDN URLs for posts,
/// reels and full carousels. The doc_id comes from DocIdProvider so rotation
/// self-heals.
/// </summary>
public sealed class GraphQlStrategy : IIgStrategy
{
    // The web app id Instagram's own frontend sends. Public knowledge, not a secret.
    private const string IgAppId = "936619743392459";

    private readonly HttpClient _http;
    private readonly DocIdProvider _docIds;
    private readonly ILogger _log;

    public GraphQlStrategy(HttpClient http, DocIdProvider docIds, ILogger log)
    {
        _http = http;
        _docIds = docIds;
        _log = log;
    }

    public string Name => "Instagram GraphQL";

    public async Task<IgMediaResult?> TryFetchAsync(IgRequest request, CancellationToken ct)
    {
        if (request.Shortcode is not { } shortcode) return null;

        try
        {
            var variables = JsonSerializer.Serialize(new
            {
                shortcode,
                fetch_tagged_user_count = (object?)null,
                hoisted_comment_id = (object?)null,
                hoisted_reply_id = (object?)null,
            });

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://www.instagram.com/graphql/query")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["av"] = "0",
                    ["__d"] = "www",
                    ["lsd"] = "AVqbxe3J_YA",
                    ["variables"] = variables,
                    ["server_timestamps"] = "true",
                    ["doc_id"] = _docIds.CurrentDocId,
                }),
            };
            httpRequest.Headers.Add("X-IG-App-ID", IgAppId);
            httpRequest.Headers.Add("X-FB-LSD", "AVqbxe3J_YA");
            httpRequest.Headers.Add("X-ASBD-ID", "129477");
            httpRequest.Headers.Add("Sec-Fetch-Site", "same-origin");
            httpRequest.Headers.Referrer = new Uri($"https://www.instagram.com/p/{shortcode}/");

            using var response = await _http.SendAsync(httpRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Instagram GraphQL returned HTTP {Code}", (int)response.StatusCode);
                await _docIds.ReportFailureAsync(shortcode, ct);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("xdt_shortcode_media", out var media) ||
                media.ValueKind != JsonValueKind.Object)
            {
                _log.LogWarning("Instagram GraphQL: no media in response (private/removed post, stale doc_id, or rate limited)");
                await _docIds.ReportFailureAsync(shortcode, ct);
                return null;
            }

            var items = new List<IgMediaItem>();

            if (media.TryGetProperty("edge_sidecar_to_children", out var sidecar) &&
                sidecar.TryGetProperty("edges", out var edges))
            {
                foreach (var edge in edges.EnumerateArray())
                    if (edge.TryGetProperty("node", out var node))
                        AddNode(node, items);
            }
            else
            {
                AddNode(media, items);
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

            if (items.Count == 0) return null;

            _docIds.ReportSuccess();
            return new IgMediaResult { Items = items, Caption = caption };
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

    private static void AddNode(JsonElement node, List<IgMediaItem> items)
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
}
