using System.Diagnostics;

namespace TelegramMediaBot.Helpers;

/// <summary>
/// Runs an external process and captures stdout/stderr. Shared by yt-dlp, ffmpeg, gallery-dl wrappers.
/// </summary>
public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, string arguments, CancellationToken ct = default)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);

        return (proc.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Start a process and return it (caller owns lifetime). Used for streaming.</summary>
    public static Process StartProcess(string fileName, string arguments)
    {
        var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        proc.Start();
        return proc;
    }
}
