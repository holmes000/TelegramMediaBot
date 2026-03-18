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
    /// Optimized: ultrafast preset, 720p, multi-threaded, low framerate for stills.
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

        var clipStartSec = audioClipStartMs / 1000.0;
        var clipDurationSec = audioClipDurationMs > 0 ? audioClipDurationMs / 1000.0 : 30.0;
        var hasAudio = audioPath is not null && File.Exists(audioPath);

        // Common video filter: 720p wide (fast encode, good for mobile), even height, yuv420p
        const string vf = "scale=720:-2:force_original_aspect_ratio=decrease,pad=720:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2:black,format=yuv420p";

        var args = new StringBuilder();

        if (hasAudio && imagePaths.Count == 1)
        {
            // Single image + audio: 1fps looped still → ultrafast encode
            args.Append($"-y -loop 1 -framerate 1 -i \"{imagePaths[0]}\" ");
            if (clipStartSec > 0) args.Append($"-ss {clipStartSec:F3} ");
            args.Append($"-i \"{audioPath}\" ");
            args.Append($"-vf \"{vf}\" ");
            args.Append("-c:v libx264 -preset ultrafast -crf 28 -tune stillimage -r 1 ");
            args.Append("-c:a aac -b:a 128k ");
            args.Append($"-t {clipDurationSec:F3} -threads 0 -movflags +faststart ");
        }
        else
        {
            // Multiple images (with or without audio): concat demuxer
            var sb = new StringBuilder();
            foreach (var img in imagePaths)
            {
                var name = Path.GetFileName(img).Replace("'", "'\\''");
                sb.AppendLine($"file '{name}'");
                sb.AppendLine($"duration {_cfg.SlideshowImageDurationSec}");
            }
            sb.AppendLine($"file '{Path.GetFileName(imagePaths[^1]).Replace("'", "'\\''")}'");
            await File.WriteAllTextAsync(concatList, sb.ToString(), ct);

            args.Append($"-y -f concat -safe 0 -i \"{concatList}\" ");

            if (hasAudio)
            {
                if (clipStartSec > 0) args.Append($"-ss {clipStartSec:F3} ");
                args.Append($"-i \"{audioPath}\" ");
                args.Append($"-vf \"{vf}\" ");
                args.Append("-fps_mode vfr -c:v libx264 -preset ultrafast -crf 28 ");
                args.Append("-c:a aac -b:a 128k -shortest -threads 0 -movflags +faststart ");
            }
            else
            {
                args.Append($"-vf \"{vf}\" ");
                args.Append("-fps_mode vfr -c:v libx264 -preset ultrafast -crf 28 -an ");
                args.Append("-threads 0 -movflags +faststart ");
            }
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
        var args = $"-y -i \"{inputPath}\" -c:v libx264 -preset ultrafast -crf 28 -c:a aac -b:a 128k -threads 0 -movflags +faststart \"{outputPath}\"";
        var (exit, _, stderr) = await ProcessRunner.RunAsync(_cfg.FfmpegPath, args, ct);
        if (exit != 0) { _log.LogError("ffmpeg reencode failed: {Err}", stderr.Length > 200 ? stderr[..200] : stderr); return null; }
        return File.Exists(outputPath) ? outputPath : null;
    }
}
