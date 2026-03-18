using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using TelegramMediaBot.Helpers;
using TelegramMediaBot.Models;
using TelegramMediaBot.Services;

namespace TelegramMediaBot.Services;

public sealed class BotUpdateHandler
{
    private readonly MediaDownloadService _dl;
    private readonly ILogger<BotUpdateHandler> _log;
    private readonly Dictionary<long, SemaphoreSlim> _chatLocks = new();
    private readonly object _lockGuard = new();

    public BotUpdateHandler(MediaDownloadService dl, ILogger<BotUpdateHandler> log) { _dl = dl; _log = log; }

    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is not { } msg) return;

        // Fire-and-forget so we don't block the polling loop.
        // Each request runs concurrently, limited by per-chat semaphore.
        _ = Task.Run(() => HandleMessageAsync(bot, msg, ct), ct);
    }

    private async Task HandleMessageAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        try
        {
            await HandleMessageCoreAsync(bot, msg, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled error for chat {Chat}", msg.Chat.Id);
        }
    }

    private async Task HandleMessageCoreAsync(ITelegramBotClient bot, Message msg, CancellationToken ct)
    {
        var chat = msg.Chat.Id;
        var text = msg.Text ?? msg.Caption ?? "";

        if (text.StartsWith("/start", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chat,
                "👋 Send me a TikTok or Instagram link and I'll download the video/slideshow for you!\n\n" +
                "Supported:\n• TikTok videos & slideshows\n• Instagram Reels, Posts, Stories & Carousels\n\n" +
                "Just paste a link and I'll handle the rest.", cancellationToken: ct);
            return;
        }
        if (text.StartsWith("/help", StringComparison.OrdinalIgnoreCase))
        {
            await bot.SendMessage(chat,
                "📖 *How to use:*\n\n" +
                "1\\. Copy a TikTok or Instagram link\n2\\. Paste it here\n3\\. Wait for the download\n\n" +
                "Slideshows are merged into a video with music if available\\.",
                parseMode: ParseMode.MarkdownV2, cancellationToken: ct);
            return;
        }

        var urls = ExtractUrls(msg);
        if (urls.Count == 0)
        {
            if (msg.Chat.Type == ChatType.Private)
                await bot.SendMessage(chat, "Send me a TikTok or Instagram link.", cancellationToken: ct);
            return;
        }

        var sem = GetSemaphore(chat);
        if (!await sem.WaitAsync(TimeSpan.Zero, ct))
        {
            await bot.SendMessage(chat, "⏳ Still processing your previous request.", cancellationToken: ct);
            return;
        }

        try
        {
            var isGroup = msg.Chat.Type is ChatType.Group or ChatType.Supergroup;
            var reply = isGroup ? null : new ReplyParameters { MessageId = msg.MessageId, AllowSendingWithoutReply = true };

            foreach (var url in urls) await ProcessUrl(bot, chat, reply, url, ct);

            // In groups, delete the original message containing the link
            if (isGroup)
            {
                try { await bot.DeleteMessage(chat, msg.MessageId, ct); } catch { }
            }
        }
        finally { sem.Release(); }
    }

    public Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _log.LogError(ex, "Telegram bot error");
        return Task.CompletedTask;
    }

    // ── Process a single URL ─────────────────────────────────────────

    private async Task ProcessUrl(ITelegramBotClient bot, long chat, ReplyParameters reply, string url, CancellationToken ct)
    {
        var status = await bot.SendMessage(chat, "⏬ Downloading...", replyParameters: reply, cancellationToken: ct);
        DownloadResult? r = null;
        try
        {
            r = await _dl.ProcessUrlAsync(url, ct);
            if (!r.Success) { await bot.EditMessageText(chat, status.MessageId, $"❌ {r.Error}", cancellationToken: ct); return; }

            // Dispatch to the right send method based on delivery type
            if      (r.IsStreamed)                    await SendStream(bot, chat, reply, r, ct);
            else if (r.MediaUrls is { Count: > 0 })  await SendByUrl(bot, chat, reply, r, ct);
            else if (r.AlbumPaths is { Count: > 0 })  await SendAlbum(bot, chat, reply, r, ct);
            else if (r.VideoPath is not null)          await SendVideo(bot, chat, reply, r, ct);
            else if (r.ImagePaths is { Count: > 0 })   await SendPhotos(bot, chat, reply, r, ct);

            try { await bot.DeleteMessage(chat, status.MessageId, ct); } catch { }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed {Url}", url);
            try { await bot.EditMessageText(chat, status.MessageId, $"❌ {ex.Message}", cancellationToken: ct); } catch { }
        }
        finally
        {
            if (r is not null) { _dl.CleanupWorkDir(r); r.Dispose(); }
        }
    }

    // ── Send methods ─────────────────────────────────────────────────

    private static async Task SendStream(ITelegramBotClient bot, long chat, ReplyParameters reply, DownloadResult r, CancellationToken ct)
    {
        await bot.SendVideo(chat, InputFile.FromStream(r.VideoStream!, "video.mp4"),
            caption: r.Caption, replyParameters: reply, supportsStreaming: true, cancellationToken: ct);
    }

    private static async Task SendByUrl(ITelegramBotClient bot, long chat, ReplyParameters reply, DownloadResult r, CancellationToken ct)
    {
        var items = r.MediaUrls!;
        if (items.Count == 1)
        {
            var (url, cat) = items[0];
            if (cat == "video") await bot.SendVideo(chat, InputFile.FromUri(url), caption: r.Caption, replyParameters: reply, supportsStreaming: true, cancellationToken: ct);
            else                await bot.SendPhoto(chat, InputFile.FromUri(url), caption: r.Caption, replyParameters: reply, cancellationToken: ct);
            return;
        }
        foreach (var batch in items.Chunk(10))
        {
            var album = BuildUrlAlbum(batch, r.Caption, items[0] == batch[0]);
            await bot.SendMediaGroup(chat, album, replyParameters: reply, cancellationToken: ct);
        }
    }

    private static async Task SendVideo(ITelegramBotClient bot, long chat, ReplyParameters reply, DownloadResult r, CancellationToken ct)
    {
        var fi = new FileInfo(r.VideoPath!);
        if (fi.Length > 50 * 1024 * 1024)
        {
            await using var s = System.IO.File.OpenRead(r.VideoPath!);
            await bot.SendDocument(chat, InputFile.FromStream(s, fi.Name), caption: r.Caption, replyParameters: reply, cancellationToken: ct);
            return;
        }
        await using var stream = System.IO.File.OpenRead(r.VideoPath!);
        await bot.SendVideo(chat, InputFile.FromStream(stream, "video.mp4"), caption: r.Caption, replyParameters: reply, supportsStreaming: true, cancellationToken: ct);
    }

    private static async Task SendPhotos(ITelegramBotClient bot, long chat, ReplyParameters reply, DownloadResult r, CancellationToken ct)
    {
        var imgs = r.ImagePaths!;
        if (imgs.Count == 1)
        {
            await using var s = System.IO.File.OpenRead(imgs[0]);
            await bot.SendPhoto(chat, InputFile.FromStream(s, Path.GetFileName(imgs[0])), caption: r.Caption, replyParameters: reply, cancellationToken: ct);
            return;
        }
        var first = true;
        foreach (var batch in imgs.Chunk(10))
        {
            var streams = new List<FileStream>();
            var album = new List<IAlbumInputMedia>();
            foreach (var p in batch)
            {
                var fs = System.IO.File.OpenRead(p);
                streams.Add(fs);
                var m = new InputMediaPhoto(InputFile.FromStream(fs, Path.GetFileName(p)));
                if (first && r.Caption is not null) { m.Caption = r.Caption; first = false; }
                album.Add(m);
            }
            await bot.SendMediaGroup(chat, album, replyParameters: reply, cancellationToken: ct);
            foreach (var fs in streams) await fs.DisposeAsync();
        }
    }

    private static async Task SendAlbum(ITelegramBotClient bot, long chat, ReplyParameters reply, DownloadResult r, CancellationToken ct)
    {
        var paths = r.AlbumPaths!;
        if (paths.Count == 1)
        {
            var ext = Path.GetExtension(paths[0]).ToLowerInvariant();
            await using var s = System.IO.File.OpenRead(paths[0]);
            if (ext is ".mp4" or ".webm" or ".mkv" or ".mov")
                await bot.SendVideo(chat, InputFile.FromStream(s, "video.mp4"), caption: r.Caption, replyParameters: reply, supportsStreaming: true, cancellationToken: ct);
            else
                await bot.SendPhoto(chat, InputFile.FromStream(s, Path.GetFileName(paths[0])), caption: r.Caption, replyParameters: reply, cancellationToken: ct);
            return;
        }
        var first = true;
        foreach (var batch in paths.Chunk(10))
        {
            var streams = new List<FileStream>();
            var album = new List<IAlbumInputMedia>();
            foreach (var p in batch)
            {
                var fs = System.IO.File.OpenRead(p);
                streams.Add(fs);
                var ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext is ".mp4" or ".webm" or ".mkv" or ".mov")
                {
                    var m = new InputMediaVideo(InputFile.FromStream(fs, Path.GetFileName(p)));
                    if (first && r.Caption is not null) { m.Caption = r.Caption; first = false; }
                    album.Add(m);
                }
                else
                {
                    var m = new InputMediaPhoto(InputFile.FromStream(fs, Path.GetFileName(p)));
                    if (first && r.Caption is not null) { m.Caption = r.Caption; first = false; }
                    album.Add(m);
                }
            }
            await bot.SendMediaGroup(chat, album, replyParameters: reply, cancellationToken: ct);
            foreach (var fs in streams) await fs.DisposeAsync();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static List<IAlbumInputMedia> BuildUrlAlbum((string Url, string Category)[] batch, string? caption, bool isFirstBatch)
    {
        var album = new List<IAlbumInputMedia>();
        var first = isFirstBatch;
        foreach (var (url, cat) in batch)
        {
            if (cat == "video")
            {
                var m = new InputMediaVideo(InputFile.FromUri(url));
                if (first && caption is not null) { m.Caption = caption; first = false; }
                album.Add(m);
            }
            else
            {
                var m = new InputMediaPhoto(InputFile.FromUri(url));
                if (first && caption is not null) { m.Caption = caption; first = false; }
                album.Add(m);
            }
        }
        return album;
    }

    private static List<string> ExtractUrls(Message msg)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var text = msg.Text ?? msg.Caption ?? "";
        var entities = msg.Entities ?? msg.CaptionEntities;
        if (entities is not null)
        {
            foreach (var e in entities)
            {
                string? u = e.Type == MessageEntityType.Url ? text.Substring(e.Offset, e.Length)
                          : e.Type == MessageEntityType.TextLink ? e.Url
                          : null;
                if (u is not null && UrlHelper.IsSupportedUrl(u)) urls.Add(u);
            }
        }
        foreach (var u in UrlHelper.ExtractUrls(text)) urls.Add(u);
        return [.. urls];
    }

    private SemaphoreSlim GetSemaphore(long chatId)
    {
        lock (_lockGuard)
        {
            if (!_chatLocks.TryGetValue(chatId, out var s))
                _chatLocks[chatId] = s = new SemaphoreSlim(2, 2);
            return s;
        }
    }
}
