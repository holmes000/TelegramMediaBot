using Telegram.Bot;
using Telegram.Bot.Polling;
using TelegramMediaBot;
using TelegramMediaBot.Models;
using TelegramMediaBot.Services;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

var cfg = new BotConfig();
builder.Configuration.GetSection(BotConfig.Section).Bind(cfg);

if (string.IsNullOrWhiteSpace(cfg.Token))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("ERROR: Set Bot__Token env var or add Token to appsettings.json");
    Console.ResetColor();
    return;
}

// Ensure directories exist
Directory.CreateDirectory(cfg.TempDir);
Directory.CreateDirectory(Path.GetDirectoryName(cfg.IgSessionFile) ?? "data");
Directory.CreateDirectory("cookies");
if (OperatingSystem.IsWindows()) Directory.CreateDirectory("tools_bin");

// Startup diagnostics
Console.WriteLine($"  Instagram auth: {(cfg.HasInstagramAuth ? "YES" : "NO")}");
Console.WriteLine($"    SessionId: {(!string.IsNullOrWhiteSpace(cfg.InstagramSessionId) ? "set" : "empty")}");
Console.WriteLine($"    Username:  {(!string.IsNullOrWhiteSpace(cfg.InstagramUsername) ? cfg.InstagramUsername : "empty")}");
Console.WriteLine($"    Session file: {cfg.IgSessionFile} ({(File.Exists(cfg.IgSessionFile) ? "exists" : "not found")})");
Console.WriteLine($"  Cookies: {(cfg.HasCookiesFile ? cfg.CookiesFile : "not found")}");
Console.WriteLine();

// Register services
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<YtDlpService>();
builder.Services.AddSingleton<GalleryDlService>();
builder.Services.AddSingleton<FfmpegService>();
builder.Services.AddSingleton<InstagramService>();
builder.Services.AddSingleton<MediaDownloadService>();
builder.Services.AddSingleton<BotUpdateHandler>();
builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(cfg.Token));
builder.Services.AddHostedService<BotHostedService>();
builder.Services.AddHostedService<TempCleanupService>();

var app = builder.Build();

Console.WriteLine("═══════════════════════════════════════════");
Console.WriteLine("  TelegramMediaBot — TikTok & Instagram   ");
Console.WriteLine("═══════════════════════════════════════════");

await app.RunAsync();

// ── Hosted service ───────────────────────────────────────────────────

file sealed class BotHostedService(
    ITelegramBotClient bot,
    BotUpdateHandler handler,
    ILogger<BotHostedService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var me = await bot.GetMe(ct);
        log.LogInformation("Bot started: @{User} ({Id})", me.Username, me.Id);
        Console.WriteLine($"  Bot: @{me.Username}");
        Console.WriteLine("  Listening for messages...\n");

        bot.StartReceiving(
            updateHandler: handler.HandleUpdateAsync,
            errorHandler: handler.HandleErrorAsync,
            receiverOptions: new ReceiverOptions
            {
                AllowedUpdates = [Telegram.Bot.Types.Enums.UpdateType.Message],
                DropPendingUpdates = true
            },
            cancellationToken: ct);

        await Task.Delay(Timeout.Infinite, ct);
    }
}

/// <summary>
/// Periodically sweeps orphaned temp directories older than 10 minutes.
/// Runs on startup and every 30 minutes.
/// </summary>
file sealed class TempCleanupService(BotConfig cfg, ILogger<TempCleanupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Sweep();
            await Task.Delay(TimeSpan.FromMinutes(30), ct);
        }
    }

    private void Sweep()
    {
        try
        {
            if (!Directory.Exists(cfg.TempDir)) return;

            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            var dirs = Directory.GetDirectories(cfg.TempDir);
            var removed = 0;

            foreach (var dir in dirs)
            {
                if (Directory.GetCreationTimeUtc(dir) < cutoff)
                {
                    try { Directory.Delete(dir, recursive: true); removed++; }
                    catch { }
                }
            }

            if (removed > 0)
                log.LogInformation("Temp cleanup: removed {Count} orphaned directories", removed);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Temp cleanup error");
        }
    }
}
