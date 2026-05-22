using System.Net.Http.Json;
using System.Text.Json;
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

public sealed class InstagramService
{
    private readonly HttpClient _http;
    private readonly ILogger<InstagramService> _log;

    private string[] _cobaltInstances = Array.Empty<string>();
    private DateTime _instancesLastFetched = DateTime.MinValue;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public InstagramService(ILogger<InstagramService> log) 
    { 
        _log = log; 
        
        // 10 second timeout: we want to fail FAST on bad instances
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public bool IsAvailable => true; 

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
                "https://co.wuk.sh",
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

    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
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

        return new IgMediaResult { Error = "All Cobalt API instances failed or blocked the request. Try again later." };
    }
}