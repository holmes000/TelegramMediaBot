using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

public sealed class YtDlpService
{
    private readonly BotConfig _cfg;
    private readonly ILogger<YtDlpService> _log;
    private readonly bool _hasCookies;

    public YtDlpService(BotConfig cfg, ILogger<YtDlpService> log)
    {
        _cfg = cfg;
        _log = log;
        _hasCookies = cfg.HasCookiesFile || !string.IsNullOrWhiteSpace(cfg.CookiesFromBrowser);
        if (_hasCookies)
            _log.LogInformation("yt-dlp cookie auth configured");
    }

    /// <summary>Get a fresh copy of cookie args for each call (prevents yt-dlp from corrupting the original).</summary>
    private string CookieArgs => _cfg.BuildSafeCookieArgs();

    public async Task<(YtDlpMeta? Meta, string? Error)> GetMetadataAsync(string url, CancellationToken ct)
    {
        var args = $"--no-download --dump-json --no-warnings --no-playlist {CookieArgs} \"{url}\"";
        var (exit, stdout, stderr) = await Run(args, ct);

        if (exit != 0)
        {
            _log.LogWarning("yt-dlp metadata failed: {Err}", Truncate(stderr, 200));

            // Don't retry for errors that won't benefit from --flat-playlist
            if (stderr.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ||
                stderr.Contains("No video formats found", StringComparison.OrdinalIgnoreCase))
                return (null, stderr);

            args = $"--no-download --dump-json --no-warnings --flat-playlist {CookieArgs} \"{url}\"";
            (exit, stdout, stderr) = await Run(args, ct);
            if (exit != 0)
            {
                _log.LogError("yt-dlp metadata retry failed: {Err}", Truncate(stderr, 200));
                return (null, stderr);
            }
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return (null, "No metadata returned");

        if (lines.Length == 1)
            return (JsonSerializer.Deserialize<YtDlpMeta>(lines[0]), null);

        var entries = lines
            .Select(l => JsonSerializer.Deserialize<YtDlpMeta>(l))
            .Where(e => e is not null)
            .Cast<YtDlpMeta>()
            .ToList();

        return (new YtDlpMeta { Id = "playlist", Type = "playlist", Entries = entries }, null);
    }

    public async Task<List<string>> DownloadAsync(string url, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var tpl = Path.Combine(outputDir, "%(id)s_%(autonumber)s.%(ext)s");
        var args = $"--no-warnings -o \"{tpl}\" --write-thumbnail --convert-thumbnails jpg --merge-output-format mp4 --no-playlist {CookieArgs} \"{url}\"";
        var (exit, _, stderr) = await Run(args, ct);
        if (exit != 0) { _log.LogError("yt-dlp download failed: {Err}", Truncate(stderr, 200)); return []; }
        return [.. Directory.GetFiles(outputDir).OrderBy(f => f)];
    }

    public async Task<List<string>> DownloadSlideshowAsync(string url, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var tpl = Path.Combine(outputDir, "%(id)s_%(autonumber)s.%(ext)s");
        var args = $"--no-warnings -o \"{tpl}\" --no-playlist {CookieArgs} \"{url}\"";
        var (exit, _, stderr) = await Run(args, ct);
        if (exit != 0) { _log.LogWarning("yt-dlp slideshow failed: {Err}", Truncate(stderr, 200)); return []; }
        return [.. Directory.GetFiles(outputDir).OrderBy(f => f)];
    }

    public async Task<string?> DownloadAudioAsync(string url, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var tpl = Path.Combine(outputDir, "audio.%(ext)s");
        var args = $"--no-warnings -x --audio-format mp3 -o \"{tpl}\" --no-playlist {CookieArgs} \"{url}\"";
        var (exit, _, _) = await Run(args, ct);
        if (exit != 0) return null;
        return Directory.GetFiles(outputDir, "audio.*").FirstOrDefault();
    }

    public Process StartStreamingDownload(string url)
    {
        var args = $"--no-warnings --no-playlist --merge-output-format mp4 {CookieArgs} -o - \"{url}\"";
        _log.LogDebug("yt-dlp stream: {Args}", args);
        return ProcessRunner.StartProcess(_cfg.YtDlpPath, args);
    }

    public bool IsLikelyVideo(YtDlpMeta? meta)
    {
        if (meta is null) return false;
        if (meta.Entries is { Count: > 1 } || meta.Type == "playlist") return false;
        if (meta.Formats is { Count: > 0 })
        {
            var hasVideo = meta.Formats.Any(f =>
                f.VideoCodec is not null && f.VideoCodec != "none" &&
                f.Extension is not ("jpg" or "png"));
            if (!hasVideo) return false;
        }
        if (meta.Extension is "jpg" or "jpeg" or "png" or "webp") return false;
        return true;
    }

    private Task<(int, string, string)> Run(string args, CancellationToken ct) =>
        ProcessRunner.RunAsync(_cfg.YtDlpPath, args, ct);

    private static string Truncate(string s, int max) => s.Length > max ? s[..max] + "..." : s;
}
