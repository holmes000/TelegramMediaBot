using System.Text;
using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

public sealed class FfmpegService
{
    private readonly BotConfig _cfg;
    private readonly ILogger<FfmpegService> _log;

    public FfmpegService(BotConfig cfg, ILogger<FfmpegService> log) { _cfg = cfg; _log = log; }

    /// <summary>
    /// Merge images into an mp4 video, optionally with an audio track trimmed to clip range.
    /// </summary>
    public async Task<string?> MergeImagesToVideoAsync(
        List<string> imagePaths, string? audioPath, string outputDir,
        int audioClipStartMs = 0, int audioClipDurationMs = 0,
        CancellationToken ct = default)
    {
        if (imagePaths.Count == 0) return null;
        Directory.CreateDirectory(outputDir);

        var concatList = Path.Combine(outputDir, "concat_list.txt");
        var outputPath = Path.Combine(outputDir, "slideshow.mp4");

        // Write concat list (filenames only — same directory)
        var sb = new StringBuilder();
        foreach (var img in imagePaths)
        {
            var name = Path.GetFileName(img).Replace("'", "'\\''");
            sb.AppendLine($"file '{name}'");
            sb.AppendLine($"duration {_cfg.SlideshowImageDurationSec}");
        }
        sb.AppendLine($"file '{Path.GetFileName(imagePaths[^1]).Replace("'", "'\\''")}'");
        await File.WriteAllTextAsync(concatList, sb.ToString(), ct);

        var clipStartSec = audioClipStartMs / 1000.0;
        var clipDurationSec = audioClipDurationMs > 0 ? audioClipDurationMs / 1000.0 : 30.0;

        var args = new StringBuilder();
        var hasAudio = audioPath is not null && File.Exists(audioPath);

        if (hasAudio && imagePaths.Count == 1)
        {
            // Single image: loop for audio duration, explicit -t to avoid hang
            args.Append($"-y -loop 1 -i \"{imagePaths[0]}\" ");
            if (clipStartSec > 0) args.Append($"-ss {clipStartSec:F3} ");
            args.Append($"-i \"{audioPath}\" ");
            args.Append("-vf \"scale=1080:-2:force_original_aspect_ratio=decrease,pad=1080:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p\" ");
            args.Append("-c:v libx264 -preset fast -crf 23 -tune stillimage ");
            args.Append("-c:a aac -b:a 192k ");
            args.Append($"-t {clipDurationSec:F3} ");
        }
        else if (hasAudio)
        {
            // Multiple images + audio
            args.Append($"-y -f concat -safe 0 -i \"{concatList}\" ");
            if (clipStartSec > 0) args.Append($"-ss {clipStartSec:F3} ");
            args.Append($"-i \"{audioPath}\" ");
            args.Append("-vf \"scale=1080:-2:force_original_aspect_ratio=decrease,pad=1080:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p\" ");
            args.Append("-fps_mode vfr -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k -shortest ");
        }
        else
        {
            // No audio
            args.Append($"-y -f concat -safe 0 -i \"{concatList}\" ");
            args.Append("-vf \"scale=1080:-2:force_original_aspect_ratio=decrease,pad=1080:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p\" ");
            args.Append("-fps_mode vfr -c:v libx264 -preset fast -crf 23 -an ");
        }

        args.Append($"\"{outputPath}\"");

        var (exit, _, stderr) = await ProcessRunner.RunAsync(_cfg.FfmpegPath, args.ToString(), ct);
        if (exit != 0) { _log.LogError("ffmpeg merge failed: {Err}", stderr.Length > 300 ? stderr[..300] : stderr); return null; }
        return File.Exists(outputPath) ? outputPath : null;
    }

    public async Task<string?> ReencodeForTelegramAsync(string inputPath, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var outputPath = Path.Combine(outputDir, "reencoded.mp4");
        var args = $"-y -i \"{inputPath}\" -c:v libx264 -preset fast -crf 23 -c:a aac -b:a 192k -movflags +faststart \"{outputPath}\"";
        var (exit, _, stderr) = await ProcessRunner.RunAsync(_cfg.FfmpegPath, args, ct);
        if (exit != 0) { _log.LogError("ffmpeg reencode failed: {Err}", stderr.Length > 200 ? stderr[..200] : stderr); return null; }
        return File.Exists(outputPath) ? outputPath : null;
    }
}
