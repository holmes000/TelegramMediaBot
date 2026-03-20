namespace TelegramMediaBot.Models;

/// <summary>
/// Bot configuration. Bound from appsettings.json section "Bot".
/// Platform-aware defaults: Windows uses tools_bin\ prefix, Linux uses bare names (expects PATH).
/// </summary>
public sealed class BotConfig
{
    public const string Section = "Bot";

    // ── Telegram ──────────────────────────────────────────────────────
    public string Token { get; set; } = "";

    // ── External tools ────────────────────────────────────────────────
    public string YtDlpPath { get; set; } = DefaultToolPath("yt-dlp");
    public string FfmpegPath { get; set; } = DefaultToolPath("ffmpeg");
    public string GalleryDlPath { get; set; } = DefaultToolPath("gallery-dl");
    public string PythonPath { get; set; } = OperatingSystem.IsWindows() ? "python" : "python3";
    public string IgScript { get; set; } = Path.Combine("scripts", "ig_media.py");

    // ── Cookies / auth ────────────────────────────────────────────────
    public string CookiesFile { get; set; } = Path.Combine("cookies", "instagram_cookies.txt");
    public string? CookiesFromBrowser { get; set; }

    // ── Instagram private API (for image-with-music audio) ────────────
    public string? InstagramSessionId { get; set; }
    public string? InstagramUsername { get; set; }
    public string? InstagramPassword { get; set; }
    public string IgSessionFile { get; set; } = Path.Combine("data", "ig_session.json");

    // ── Processing ────────────────────────────────────────────────────
    public string TempDir { get; set; } = Path.Combine("data", "temp");
    public int MaxFileSizeMb { get; set; } = 50;
    public int SlideshowImageDurationSec { get; set; } = 3;

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>Returns true if any Instagram login method is configured.</summary>
    public bool HasInstagramAuth =>
        !string.IsNullOrWhiteSpace(InstagramSessionId) ||
        (!string.IsNullOrWhiteSpace(InstagramUsername) && !string.IsNullOrWhiteSpace(InstagramPassword)) ||
        File.Exists(IgSessionFile);

    /// <summary>Returns true if a valid Netscape cookies file exists.</summary>
    public bool HasCookiesFile
    {
        get
        {
            if (!File.Exists(CookiesFile)) return false;
            try
            {
                var firstLine = File.ReadLines(CookiesFile).FirstOrDefault() ?? "";
                // Netscape cookies.txt starts with this header or a domain line with tabs
                return firstLine.StartsWith("# Netscape HTTP Cookie") ||
                       firstLine.StartsWith("# HTTP Cookie") ||
                       firstLine.Contains('\t');
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// Builds the yt-dlp / gallery-dl cookie argument string.
    /// </summary>
    public string BuildCookieArgs()
    {
        if (HasCookiesFile)
            return $"--cookies \"{CookiesFile}\"";
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser))
            return $"--cookies-from-browser {CookiesFromBrowser}";
        return "";
    }

    /// <summary>
    /// Creates a disposable copy of the cookies file and returns args pointing to it.
    /// Prevents yt-dlp from overwriting the original cookies file.
    /// Returns empty string if no cookies configured.
    /// </summary>
    public string BuildSafeCookieArgs()
    {
        if (HasCookiesFile)
        {
            try
            {
                Directory.CreateDirectory(TempDir);
                var copy = Path.Combine(TempDir, $"cookies_{Guid.NewGuid():N}.txt");
                File.Copy(CookiesFile, copy, overwrite: true);
                return $"--cookies \"{copy}\"";
            }
            catch { return $"--cookies \"{CookiesFile}\""; }
        }
        if (!string.IsNullOrWhiteSpace(CookiesFromBrowser))
            return $"--cookies-from-browser {CookiesFromBrowser}";
        return "";
    }

    private static string DefaultToolPath(string tool)
    {
        if (!OperatingSystem.IsWindows()) return tool;
        // On Windows, default to tools_bin\ subfolder
        return Path.Combine("tools_bin", tool + ".exe");
    }
}
