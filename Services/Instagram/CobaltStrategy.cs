using System.Net.Http.Json;
using System.Text.Json;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Tiers 4+5: Cobalt instances. The self-hosted sidecar (CobaltLocalUrl) is
/// tried first — it runs the Cobalt team's continuously-updated extraction on
/// our own IP. Public instances from cobalt.directory are the true last
/// resort; their shared datacenter IPs are usually blocked by Instagram.
/// </summary>
public sealed class CobaltStrategy : IIgStrategy
{
    private readonly HttpClient _http;
    private readonly BotConfig _cfg;
    private readonly ILogger _log;

    private string[] _publicInstances = Array.Empty<string>();
    private DateTime _instancesLastFetched = DateTime.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public CobaltStrategy(HttpClient http, BotConfig cfg, ILogger log)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
    }

    public string Name => "Cobalt";

    public async Task<IgMediaResult?> TryFetchAsync(IgRequest request, CancellationToken ct)
    {
        var local = string.IsNullOrWhiteSpace(_cfg.CobaltLocalUrl) ? null : _cfg.CobaltLocalUrl.TrimEnd('/');

        if (local is not null)
        {
            var result = await TryInstanceAsync(local, request.Url, isLocal: true, ct);
            if (result is { Items.Count: > 0 }) return result;
        }

        foreach (var instance in await GetPublicInstancesAsync(ct))
        {
            var result = await TryInstanceAsync(instance, request.Url, isLocal: false, ct);
            if (result is { Items.Count: > 0 }) return result;
        }

        return null;
    }

    private async Task<IgMediaResult?> TryInstanceAsync(string instance, string url, bool isLocal, CancellationToken ct)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, instance + "/");
            // Cobalt strictly requires Accept: application/json — per-request header
            // overrides the shared client's Accept: */*, which Cobalt rejects with 400
            request.Headers.Accept.ParseAdd("application/json");
            if (!isLocal)
            {
                // Browser-ish headers to get past public instances' WAFs
                request.Headers.Add("Origin", "https://cobalt.tools");
                request.Headers.Add("Referer", "https://cobalt.tools/");
            }
            request.Content = JsonContent.Create(new { url });
            // Some Cobalt builds match Content-Type exactly — strip "; charset=utf-8"
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // The local sidecar should always answer — surface its failures.
                // Cobalt uses 400 for both bad requests and extraction errors; the
                // JSON body's error.code says which, so log it.
                if (isLocal)
                {
                    var body = await SafeReadAsync(response, ct);
                    _log.LogWarning("Local Cobalt {Instance} returned HTTP {Code}: {Body}",
                        instance, (int)response.StatusCode, body);
                }
                return null; // Public instances: skip silently to burn through bad ones fast
            }

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
                return null;
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
                foreach (var item in root.GetProperty("picker").EnumerateArray())
                {
                    var type = item.GetProperty("type").GetString() == "photo" ? "image" : "video";
                    var mediaUrl = item.GetProperty("url").GetString();

                    if (!string.IsNullOrEmpty(mediaUrl))
                        items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
                }
            }

            if (items.Count == 0) return null;

            _log.LogInformation("Extracted {N} items via Cobalt {Instance}", items.Count, instance);
            return new IgMediaResult { Items = items };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // Timeout — move on
        }
        catch (Exception)
        {
            return null; // Network error or bad JSON — skip
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length > 300 ? body[..300] + "..." : body;
        }
        catch
        {
            return "(unreadable body)";
        }
    }

    private async Task<string[]> GetPublicInstancesAsync(CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            // Cache for 1 hour to keep the bot lightning fast
            if (DateTime.UtcNow - _instancesLastFetched < TimeSpan.FromHours(1) && _publicInstances.Length > 0)
                return _publicInstances;

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

                    if (doc.RootElement.TryGetProperty("data", out var dataProp) &&
                        dataProp.TryGetProperty("instagram", out var igInstances))
                    {
                        foreach (var item in igInstances.EnumerateArray())
                        {
                            var url = item.GetString();
                            if (!string.IsNullOrEmpty(url) && url.StartsWith("http"))
                                list.Add(url);
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

            // Known instances appended in case the directory is down or empty
            var fallbacks = new[]
            {
                "https://api.cobalt.blackcat.sweeux.org",
                "https://cobalt.canine.tools",
                "https://api.cobalt.tools" // The official API
            };

            foreach (var fallback in fallbacks)
                list.Add(fallback);

            // Clean up: trailing slashes, keep official last to spread load, limit size
            _publicInstances = list
                .Select(url => url.TrimEnd('/'))
                .Distinct()
                .Where(url => !url.Contains("api.cobalt.tools"))
                .Append("https://api.cobalt.tools")
                .Take(20)
                .ToArray();

            _instancesLastFetched = DateTime.UtcNow;
            _log.LogInformation("Cached {Count} public Cobalt instances", _publicInstances.Length);

            return _publicInstances;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
