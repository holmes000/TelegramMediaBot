using System.Text;
using Telegram.Bot;
using TelegramMediaBot.Models;

namespace TelegramMediaBot.Services;

/// <summary>
/// Periodically exercises every Instagram extraction tier against a known
/// public post so broken tiers are noticed before users hit them. Logs the
/// per-tier report; if AdminChatId is configured, sends a Telegram alert when
/// all tiers fail or tier availability changes.
/// </summary>
public sealed class IgCanaryService(
    InstagramService ig,
    BotConfig cfg,
    ITelegramBotClient bot,
    ILogger<IgCanaryService> log) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private string? _lastSummary;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                await RunOnceAsync(ct);
                await Task.Delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var results = await ig.RunDiagnosticsAsync(cfg.CanaryUrl, ct);

            foreach (var (tier, ok, error) in results)
            {
                if (ok) log.LogInformation("Canary: {Tier} OK", tier);
                else log.LogWarning("Canary: {Tier} FAILED ({Error})", tier, error);
            }

            var summary = string.Join("\n", results.Select(r => $"{(r.Ok ? "✅" : "❌")} {r.Tier}"));
            var allFailed = results.All(r => !r.Ok);
            var changed = _lastSummary is not null && summary != _lastSummary;

            if (cfg.AdminChat is { } adminChat && (allFailed || changed))
            {
                var text = new StringBuilder()
                    .AppendLine(allFailed
                        ? "🚨 Instagram canary: ALL extraction tiers are failing!"
                        : "⚠️ Instagram canary: tier availability changed.")
                    .AppendLine()
                    .AppendLine(summary)
                    .Append("\nTest post: ").Append(cfg.CanaryUrl)
                    .ToString();

                try
                {
                    await bot.SendMessage(adminChat, text, cancellationToken: ct);
                }
                catch (Exception ex)
                {
                    log.LogWarning("Canary: failed to notify admin chat {Chat}: {Msg}", adminChat, ex.Message);
                }
            }

            _lastSummary = summary;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Canary run failed");
        }
    }
}
