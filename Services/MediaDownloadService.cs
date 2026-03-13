using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

/// <summary>
/// Orchestrator — routes URLs to the optimal tool chain:
///
/// Instagram (private API — fastest):
///   • Single API call gets all media URLs + audio + caption
///   • Videos/images without audio → send CDN URLs directly (no disk)
///   • Images with audio → download + ffmpeg merge (disk)
///   • Falls back to gallery-dl if private API unavailable
///
/// TikTok:
///   • Videos → yt-dlp stream (no disk)
///   • Slideshows → gallery-dl download → ffmpeg merge (disk)
/// </summary>
public sealed class MediaDownloadService
{
    private readonly YtDlpService _ytDlp;
    private readonly GalleryDlService _galleryDl;
    private readonly FfmpegService _ffmpeg;
    private readonly InstagramService _ig;
    private readonly BotConfig _cfg;
    private readonly ILogger<MediaDownloadService> _log;

    public MediaDownloadService(
        YtDlpService ytDlp, GalleryDlService galleryDl, FfmpegService ffmpeg,
        InstagramService ig, BotConfig cfg, ILogger<MediaDownloadService> log)
    {
        _ytDlp = ytDlp; _galleryDl = galleryDl; _ffmpeg = ffmpeg;
        _ig = ig; _cfg = cfg; _log = log;
    }

    public async Task<DownloadResult> ProcessUrlAsync(string url, CancellationToken ct)
    {
        var job = Guid.NewGuid().ToString("N")[..8];
        try
        {
            _log.LogInformation("[{Job}] {Url}", job, url);

            // ── Instagram → private API (one call, fastest) ──────────
            if (UrlHelper.IsInstagramUrl(url) && _ig.IsAvailable)
            {
                _log.LogInformation("[{Job}] Instagram → private API", job);
                return await ViaInstagramApi(url, job, ct);
            }

            // ── TikTok photo/slideshow → gallery-dl ──────────────────
            if (UrlHelper.IsLikelyPhotoUrl(url))
            {
                _log.LogInformation("[{Job}] TikTok photo → gallery-dl", job);
                return await ViaGalleryDl(url, job, null, ct);
            }

            // ── Everything else → yt-dlp ─────────────────────────────
            return await ViaYtDlp(url, job, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[{Job}] Failed", job);
            return DownloadResult.Fail($"Error: {ex.Message}");
        }
    }

    public void CleanupWorkDir(DownloadResult result)
    {
        try
        {
            if (result.IsStreamed) return;
            var path = result.VideoPath ?? result.AlbumPaths?.FirstOrDefault() ?? result.ImagePaths?.FirstOrDefault();
            if (path is null) return;
            var dir = Path.GetDirectoryName(path);
            var tempDir = Path.GetFullPath(_cfg.TempDir);
            if (dir is not null && Path.GetFullPath(dir).StartsWith(tempDir, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Instagram private API path
    // ═══════════════════════════════════════════════════════════════════

    private async Task<DownloadResult> ViaInstagramApi(string url, string job, CancellationToken ct)
    {
        // Step 1: Get all media info (URLs only — no disk)
        var info = await _ig.GetMediaInfoAsync(url, ct);

        if (info.HasError)
        {
            _log.LogWarning("[{Job}] Instagram API error: {Err} — falling back to gallery-dl", job, info.Error);
            return await ViaGalleryDl(url, job, null, ct);
        }

        if (info.Items.Count == 0)
            return DownloadResult.Fail("No media found in this post.");

        var caption = info.Caption;

        // Step 2: Decide if we need disk (audio merge) or can send URLs directly

        // Case: has images + audio → need ffmpeg merge (disk path)
        if (info.HasImages && info.HasAudio)
        {
            _log.LogInformation("[{Job}] Images + audio → disk for merge", job);
            return await ViaInstagramApiDisk(url, job, ct);
        }

        // Case: no audio needed → send CDN URLs directly (no disk!)
        var mediaUrls = info.Items
            .Select(i => (i.Url, i.Type))
            .ToList();

        _log.LogInformation("[{Job}] Sending {N} items via URL (no disk)", job, mediaUrls.Count);
        return new DownloadResult { Success = true, MediaUrls = mediaUrls };
    }

    private async Task<DownloadResult> ViaInstagramApiDisk(string url, string job, CancellationToken ct)
    {
        var dir = MakeWorkDir(job);

        // Download everything to disk (images + audio)
        var info = await _ig.DownloadMediaAsync(url, dir, ct);

        if (info.HasError || info.Items.Count == 0)
            return DownloadResult.Fail(info.Error ?? "Download failed.");

        var caption = info.Caption;
        var vids = info.Items.Where(i => i.Type == "video" && i.Path is not null).ToList();
        var imgs = info.Items.Where(i => i.Type == "image" && i.Path is not null).ToList();
        var audioPath = info.Audio?.Path;
        var clipStart = info.Audio?.StartMs ?? 0;
        var clipDur = info.Audio?.DurationMs ?? 0;

        _log.LogInformation("[{Job}] Downloaded {V}vid {I}img audio={HasAudio}",
            job, vids.Count, imgs.Count, audioPath is not null);

        // Merge images + audio into slideshow video
        string? slideshow = null;
        if (imgs.Count > 0 && audioPath is not null)
        {
            var imgPaths = imgs.Select(i => i.Path!).ToList();
            _log.LogInformation("[{Job}] Merging {N} images + audio (clip {S}ms+{D}ms)",
                job, imgPaths.Count, clipStart, clipDur);
            slideshow = await _ffmpeg.MergeImagesToVideoAsync(imgPaths, audioPath, dir, clipStart, clipDur, ct);
        }

        // Build result
        if (slideshow is not null)
        {
            if (vids.Count == 0)
                return Ok(videoPath: slideshow, caption: caption);

            // Mixed carousel: slideshow replaces images, keep original videos
            var album = new List<string>();
            var slideshowInserted = false;
            foreach (var item in info.Items)
            {
                if (item.Type == "image")
                {
                    if (!slideshowInserted) { album.Add(slideshow); slideshowInserted = true; }
                }
                else if (item.Path is not null)
                {
                    album.Add(item.Path);
                }
            }
            return album.Count == 1 ? Ok(videoPath: album[0], caption: caption) : Ok(albumPaths: album, caption: caption);
        }

        // No audio merge — build from downloaded files
        var allPaths = info.Items.Where(i => i.Path is not null).Select(i => i.Path!).ToList();

        if (vids.Count > 0 && imgs.Count > 0) return Ok(albumPaths: allPaths, caption: caption);
        if (vids.Count == 1 && imgs.Count == 0) return Ok(videoPath: vids[0].Path!, caption: caption);
        if (vids.Count > 1 && imgs.Count == 0) return Ok(albumPaths: vids.Select(v => v.Path!).ToList(), caption: caption);
        if (imgs.Count > 0) return Ok(imagePaths: imgs.Select(i => i.Path!).ToList(), caption: caption);

        return DownloadResult.Fail("No media files downloaded.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // yt-dlp path (TikTok videos, fallback)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<DownloadResult> ViaYtDlp(string url, string job, CancellationToken ct)
    {
        var (meta, err) = await _ytDlp.GetMetadataAsync(url, ct);
        string? caption = null;

        if (meta is null)
        {
            var cantHandle =
                err?.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) == true ||
                err?.Contains("no video", StringComparison.OrdinalIgnoreCase) == true ||
                err?.Contains("No video formats found", StringComparison.OrdinalIgnoreCase) == true;

            if (cantHandle)
            {
                _log.LogInformation("[{Job}] yt-dlp can't handle → gallery-dl", job);
                return await ViaGalleryDl(url, job, caption, ct);
            }

            _log.LogWarning("[{Job}] yt-dlp metadata failed → disk fallback", job);
            return await ViaYtDlpDisk(url, job, caption, ct);
        }

        if (_ytDlp.IsLikelyVideo(meta))
        {
            _log.LogInformation("[{Job}] Video → streaming", job);
            var proc = _ytDlp.StartStreamingDownload(url);
            return new DownloadResult
            {
                Success = true,
                VideoStream = proc.StandardOutput.BaseStream,
                StreamProcess = proc,
            };
        }

        return await ViaYtDlpDisk(url, job, caption, ct);
    }

    private async Task<DownloadResult> ViaYtDlpDisk(string url, string job, string? caption, CancellationToken ct)
    {
        var dir = MakeWorkDir(job);

        var files = await _ytDlp.DownloadSlideshowAsync(url, dir, ct);
        if (files.Count == 0) files = await _ytDlp.DownloadAsync(url, dir, ct);
        if (files.Count == 0) files = await _galleryDl.DownloadAsync(url, dir, ct);
        if (files.Count == 0) return DownloadResult.Fail("Could not download any media.");

        var vids = files.Where(FileTypeHelper.IsVideo).ToList();
        var imgs = files.Where(FileTypeHelper.IsImage).OrderBy(f => f).ToList();
        var auds = files.Where(FileTypeHelper.IsAudio).ToList();

        if (vids.Count > 0)
        {
            var v = vids[0];
            if (new FileInfo(v).Length > _cfg.MaxFileSizeMb * 1024L * 1024L || !v.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                v = await _ffmpeg.ReencodeForTelegramAsync(v, dir, ct) ?? v;
            return Ok(videoPath: v, caption: caption);
        }

        if (imgs.Count > 0)
        {
            var audio = auds.FirstOrDefault() ?? await _ytDlp.DownloadAudioAsync(url, Path.Combine(dir, "audio"), ct);
            var video = await _ffmpeg.MergeImagesToVideoAsync(imgs, audio, dir, ct: ct);
            if (video is not null) return Ok(videoPath: video, caption: caption);
            return Ok(imagePaths: imgs, caption: caption);
        }

        return DownloadResult.Fail("No recognized media.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // gallery-dl path (fallback for Instagram without auth, TikTok photos)
    // ═══════════════════════════════════════════════════════════════════

    private async Task<DownloadResult> ViaGalleryDl(string url, string job, string? caption, CancellationToken ct)
    {
        var items = await _galleryDl.GetMediaUrlsAsync(url, ct);

        if (items.Count == 0)
        {
            _log.LogInformation("[{Job}] gallery-dl URLs empty → disk", job);
            return await ViaGalleryDlDisk(url, job, caption, ct);
        }

        var hasImages = items.Any(i => i.Category == "image");
        var hasAudio = items.Any(i => i.Category == "audio");

        if (hasImages && hasAudio)
        {
            _log.LogInformation("[{Job}] Audio present → disk for merge", job);
            return await ViaGalleryDlDisk(url, job, caption, ct);
        }

        var urls = items.Where(i => i.Category is "image" or "video").Select(i => (i.Url, i.Category)).ToList();
        if (urls.Count == 0) return DownloadResult.Fail("No media found.");

        _log.LogInformation("[{Job}] {N} items via URL (no disk)", job, urls.Count);
        return new DownloadResult { Success = true, MediaUrls = urls };
    }

    private async Task<DownloadResult> ViaGalleryDlDisk(string url, string job, string? caption, CancellationToken ct)
    {
        var dir = MakeWorkDir(job);
        var files = await _galleryDl.DownloadAsync(url, dir, ct);
        if (files.Count == 0) return DownloadResult.Fail("gallery-dl downloaded nothing.");

        var vids = files.Where(FileTypeHelper.IsVideo).ToList();
        var imgs = files.Where(FileTypeHelper.IsImage).OrderBy(f => f).ToList();
        var auds = files.Where(FileTypeHelper.IsAudio).ToList();

        string? audioPath = auds.FirstOrDefault();
        string? slideshow = null;

        if (imgs.Count > 0 && audioPath is not null)
        {
            slideshow = await _ffmpeg.MergeImagesToVideoAsync(imgs, audioPath, dir, ct: ct);
        }

        if (slideshow is not null)
        {
            if (vids.Count == 0) return Ok(videoPath: slideshow, caption: caption);
            var album = new List<string>();
            var replaced = false;
            foreach (var f in files.Where(FileTypeHelper.IsMedia))
            {
                if (FileTypeHelper.IsImage(f)) { if (!replaced) { album.Add(slideshow); replaced = true; } }
                else album.Add(f);
            }
            return album.Count == 1 ? Ok(videoPath: album[0], caption: caption) : Ok(albumPaths: album, caption: caption);
        }

        if (imgs.Count > 0 && vids.Count == 0) return Ok(imagePaths: imgs, caption: caption);
        if (vids.Count > 0 && imgs.Count > 0) return Ok(albumPaths: files.Where(FileTypeHelper.IsMedia).ToList(), caption: caption);
        if (vids.Count == 1) return Ok(videoPath: vids[0], caption: caption);
        if (vids.Count > 1) return Ok(albumPaths: vids, caption: caption);

        return DownloadResult.Fail("No recognized media.");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private string MakeWorkDir(string job)
    {
        var dir = Path.Combine(_cfg.TempDir, job);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static DownloadResult Ok(string? videoPath = null, List<string>? imagePaths = null,
        List<string>? albumPaths = null, string? caption = null) =>
        new() { Success = true, VideoPath = videoPath, ImagePaths = imagePaths, AlbumPaths = albumPaths };

}
