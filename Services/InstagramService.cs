using System.Text.Json;
using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

/// <summary>
/// Result from the Instagram private API — all media from a single post in one call.
/// </summary>
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
    public string Type { get; init; } = "";   // "image" or "video"
    public string Url { get; init; } = "";
    public string? Path { get; init; }        // set when downloaded to disk
}

public sealed class IgAudioInfo
{
    public string? Url { get; init; }
    public string? Path { get; init; }        // set when downloaded to disk
    public int StartMs { get; init; }
    public int DurationMs { get; init; }
}

/// <summary>
/// Unified Instagram service using the private (mobile) API via instagrapi.
/// One API call gets everything: videos, images, audio + clip timing, captions.
/// Replaces yt-dlp + gallery-dl + audio extraction for all Instagram URLs.
/// </summary>
public sealed class InstagramService
{
    private readonly BotConfig _cfg;
    private readonly ILogger<InstagramService> _log;

    public InstagramService(BotConfig cfg, ILogger<InstagramService> log) { _cfg = cfg; _log = log; }

    /// <summary>
    /// Fetch all media info from an Instagram post. Returns URLs (no disk I/O).
    /// </summary>
    public async Task<IgMediaResult> GetMediaInfoAsync(string url, CancellationToken ct)
    {
        return await RunScript(url, downloadDir: null, ct);
    }

    /// <summary>
    /// Fetch and download all media from an Instagram post to disk.
    /// Used when ffmpeg processing is needed (image + audio merge).
    /// </summary>
    public async Task<IgMediaResult> DownloadMediaAsync(string url, string outputDir, CancellationToken ct)
    {
        return await RunScript(url, downloadDir: outputDir, ct);
    }

    public bool IsAvailable => _cfg.HasInstagramAuth;

    private async Task<IgMediaResult> RunScript(string url, string? downloadDir, CancellationToken ct)
    {
        if (!_cfg.HasInstagramAuth)
            return new IgMediaResult { Error = "No Instagram credentials configured" };

        var args = BuildArgs(url, downloadDir);
        _log.LogInformation("Instagram API: {Url}", url);

        var (exit, stdout, stderr) = await ProcessRunner.RunAsync(_cfg.PythonPath, args, ct);

        if (stderr.Length > 0)
            _log.LogWarning("ig_media.py stderr: {Err}", stderr.Length > 500 ? stderr[..500] : stderr);

        if (exit != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _log.LogWarning("ig_media.py failed (exit {Code}), stdout: {Out}", exit,
                stdout.Length > 200 ? stdout[..200] : stdout);
            return new IgMediaResult { Error = $"Script failed (exit {exit})" };
        }

        try
        {
            return ParseResult(stdout.Trim());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to parse ig_media.py output");
            return new IgMediaResult { Error = "Failed to parse script output" };
        }
    }

    private IgMediaResult ParseResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errProp))
            return new IgMediaResult { Error = errProp.GetString() };

        var caption = root.TryGetProperty("caption", out var cap) ? cap.GetString() : null;

        var items = new List<IgMediaItem>();
        if (root.TryGetProperty("items", out var itemsArr))
        {
            foreach (var item in itemsArr.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString() ?? "";
                var url = item.GetProperty("url").GetString() ?? "";
                var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (!string.IsNullOrEmpty(url))
                    items.Add(new IgMediaItem { Type = type, Url = url, Path = path });
            }
        }

        IgAudioInfo? audio = null;
        if (root.TryGetProperty("audio", out var audioProp))
        {
            var audioUrl = audioProp.TryGetProperty("url", out var au) ? au.GetString() : null;
            var audioPath = audioProp.TryGetProperty("path", out var ap) ? ap.GetString() : null;
            var startMs = audioProp.TryGetProperty("start_ms", out var s) ? s.GetInt32() : 0;
            var durMs = audioProp.TryGetProperty("duration_ms", out var d) ? d.GetInt32() : 0;

            if (!string.IsNullOrEmpty(audioUrl))
                audio = new IgAudioInfo { Url = audioUrl, Path = audioPath, StartMs = startMs, DurationMs = durMs };
        }

        _log.LogInformation("Instagram API: {N} items ({V}vid {I}img), audio={HasAudio}",
            items.Count, items.Count(i => i.Type == "video"), items.Count(i => i.Type == "image"),
            audio is not null);

        return new IgMediaResult { Caption = caption, Items = items, Audio = audio };
    }

    private string BuildArgs(string url, string? downloadDir)
    {
        var args = $"\"{_cfg.IgScript}\" \"{url}\" --session \"{_cfg.IgSessionFile}\"";

        if (downloadDir is not null)
            args += $" --download-dir \"{downloadDir}\"";

        if (!string.IsNullOrWhiteSpace(_cfg.InstagramSessionId))
            args += $" --sessionid \"{_cfg.InstagramSessionId}\"";
        else if (!string.IsNullOrWhiteSpace(_cfg.InstagramUsername))
            args += $" --username \"{_cfg.InstagramUsername}\" --password \"{_cfg.InstagramPassword}\"";

        return args;
    }
}
