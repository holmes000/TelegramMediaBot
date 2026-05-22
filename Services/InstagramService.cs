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

    // A rotating list of public community instances to bypass AWS blocks
    private readonly string[] _cobaltInstances = 
    {
        "https://cobalt-api.pewpew.dev",
        "https://api.cobalt.my.id",
        "https://cobalt.api.timelessnesses.me",
        "https://api.cobalt.tools" // Official as absolute last resort
    };

    public InstagramService(ILogger<InstagramService> log) 
    { 
        _log = log; 
        _http = new HttpClient();
        
        // Cobalt v10 requires strict JSON Accept headers
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
    }

    public bool IsAvailable => true; 

    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        var payload = new { url = url };
        
        foreach (var instance in _cobaltInstances)
        {
            try
            {
                _log.LogInformation("Trying Cobalt API: {Instance} for {Url}", instance, url);
                
                // Cobalt v10 uses the root endpoint "/", NOT "/api/json"
                var response = await _http.PostAsJsonAsync(instance + "/", payload, ct);
                
                if (!response.IsSuccessStatusCode)
                {
                    _log.LogWarning("Cobalt API {Instance} failed with HTTP {Code}", instance, response.StatusCode);
                    continue; // Move to next instance
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var status = root.GetProperty("status").GetString();
                var items = new List<IgMediaItem>();

                if (status == "error")
                {
                    // v10 nests the error code inside an error object
                    var text = root.TryGetProperty("error", out var errObj) && errObj.TryGetProperty("code", out var code) 
                        ? code.GetString() 
                        : "Unknown error";
                    _log.LogWarning("Cobalt {Instance} returned error: {Err}", instance, text);
                    continue; // Move to next instance
                }

                // v10 returns "tunnel" or "redirect" for single media
                if (status is "tunnel" or "redirect")
                {
                    var mediaUrl = root.GetProperty("url").GetString();
                    var type = (mediaUrl?.Contains(".jpg") == true || mediaUrl?.Contains(".webp") == true) ? "image" : "video";
                    
                    if (!string.IsNullOrEmpty(mediaUrl))
                        items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
                }
                // v10 carousels
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
            catch (Exception ex)
            {
                _log.LogError("Exception reaching {Instance}: {Msg}", instance, ex.Message);
            }
        }

        return new IgMediaResult { Error = "All Cobalt API instances failed or blocked the request." };
    }
}