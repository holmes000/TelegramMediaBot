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

    public InstagramService(ILogger<InstagramService> log) 
    { 
        _log = log; 
        _http = new HttpClient();
        
        // Cobalt requires these specific headers to accept requests
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TelegramMediaBot/1.0 (Contact: your@email.com)");
    }

    // Always true now, no login credentials required!
    public bool IsAvailable => true; 

    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        try
        {
            _log.LogInformation("Cobalt API Requested: {Url}", url);
            
            var payload = new { url = url };
            var response = await _http.PostAsJsonAsync("https://api.cobalt.tools/api/json", payload, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var errText = await response.Content.ReadAsStringAsync(ct);
                _log.LogWarning("Cobalt API failed: {Code} {Err}", response.StatusCode, errText);
                return new IgMediaResult { Error = "Failed to extract media from Instagram." };
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.GetProperty("status").GetString();
            var items = new List<IgMediaItem>();

            if (status == "error")
            {
                var text = root.TryGetProperty("text", out var t) ? t.GetString() : "Unknown error";
                return new IgMediaResult { Error = text };
            }

            if (status is "redirect" or "stream" or "success")
            {
                // Single Reel or Image
                var mediaUrl = root.GetProperty("url").GetString();
                var type = (mediaUrl?.Contains(".jpg") == true || mediaUrl?.Contains(".webp") == true) ? "image" : "video";
                
                if (!string.IsNullOrEmpty(mediaUrl))
                    items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
            }
            else if (status == "picker")
            {
                // Carousel / Slideshow
                var picker = root.GetProperty("picker").EnumerateArray();
                foreach (var item in picker)
                {
                    var itemType = item.GetProperty("type").GetString(); 
                    var type = itemType == "photo" ? "image" : "video";
                    var mediaUrl = item.GetProperty("url").GetString();
                    
                    if (!string.IsNullOrEmpty(mediaUrl))
                        items.Add(new IgMediaItem { Type = type, Url = mediaUrl });
                }
            }

            _log.LogInformation("Cobalt API: extracted {N} items", items.Count);
            
            // Audio is intentionally null because Cobalt pre-merges audio into video files.
            // This forces MediaDownloadService to use the lightning-fast "URL-only" delivery path.
            return new IgMediaResult { Items = items, Audio = null, Caption = null };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cobalt API error");
            return new IgMediaResult { Error = "Service temporarily unavailable." };
        }
    }
}