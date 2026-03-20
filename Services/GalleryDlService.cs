using System.Text;
using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

public sealed class GalleryDlService
{
    private readonly BotConfig _cfg;
    private readonly ILogger<GalleryDlService> _log;

    public GalleryDlService(BotConfig cfg, ILogger<GalleryDlService> log)
    {
        _cfg = cfg;
        _log = log;
    }

    private string CookieArgs => _cfg.BuildSafeCookieArgs();

    /// <summary>Extract direct CDN URLs without downloading (fast, no disk I/O).</summary>
    public async Task<List<GalleryDlMediaItem>> GetMediaUrlsAsync(string url, CancellationToken ct)
    {
        var (exit, stdout, stderr) = await Run($"--dump-json {CookieArgs} \"{url}\"", ct);
        if (exit != 0) { _log.LogWarning("gallery-dl --dump-json failed: {Err}", Trunc(stderr)); return []; }

        var items = new List<GalleryDlMediaItem>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var root = doc.RootElement;
                string? mediaUrl = null;
                string ext = "";

                if (root.ValueKind == System.Text.Json.JsonValueKind.Array && root.GetArrayLength() >= 3)
                {
                    if (root[2].ValueKind == System.Text.Json.JsonValueKind.String)
                        mediaUrl = root[2].GetString();
                    var fn = root[1].GetString() ?? "";
                    var dot = fn.LastIndexOf('.');
                    if (dot >= 0) ext = fn[(dot + 1)..].ToLowerInvariant();
                }
                else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (root.TryGetProperty("url", out var u)) mediaUrl = u.GetString();
                    if (root.TryGetProperty("extension", out var e)) ext = (e.GetString() ?? "").ToLowerInvariant();
                }

                if (string.IsNullOrWhiteSpace(mediaUrl) || !mediaUrl.StartsWith("http")) continue;
                if (string.IsNullOrEmpty(ext)) ext = GuessExt(mediaUrl);

                items.Add(new GalleryDlMediaItem(mediaUrl, ext, FileTypeHelper.Classify(ext)));
            }
            catch { }
        }

        _log.LogInformation("gallery-dl URLs: {N} ({Img}img {Vid}vid {Aud}aud)",
            items.Count, items.Count(i => i.Category == "image"),
            items.Count(i => i.Category == "video"), items.Count(i => i.Category == "audio"));
        return items;
    }

    /// <summary>Download all media to disk (needed when ffmpeg merge is required).</summary>
    public async Task<List<string>> DownloadAsync(string url, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var args = new StringBuilder();
        args.Append($"-d \"{outputDir}\" -o filename={{num:>03}}.{{extension}} -o directory=[] --no-mtime ");
        if (CookieArgs.Length > 0) args.Append($"{CookieArgs} ");
        args.Append($"\"{url}\"");

        var (exit, _, stderr) = await Run(args.ToString(), ct);
        if (exit != 0)
        {
            _log.LogWarning("gallery-dl download failed: {Err}", Trunc(stderr));
            var partial = Collect(outputDir);
            if (partial.Count > 0) { _log.LogInformation("gallery-dl: partial {N} files", partial.Count); return partial; }
            return [];
        }

        var files = Collect(outputDir);
        _log.LogInformation("gallery-dl: downloaded {N} files", files.Count);
        return files;
    }

    private static List<string> Collect(string dir) =>
        !Directory.Exists(dir) ? [] :
        [.. Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => FileTypeHelper.IsMedia(f) || FileTypeHelper.IsAudio(f))
            .OrderBy(f => f)];

    private static string GuessExt(string url)
    {
        var path = url.Split('?')[0];
        var dot = path.LastIndexOf('.');
        return dot >= 0 && dot < path.Length - 1 ? path[(dot + 1)..].ToLowerInvariant() : "";
    }

    private Task<(int, string, string)> Run(string args, CancellationToken ct) =>
        ProcessRunner.RunAsync(_cfg.GalleryDlPath, args, ct);

    private static string Trunc(string s) => s.Length > 200 ? s[..200] + "..." : s;
}
