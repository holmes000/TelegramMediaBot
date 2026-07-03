# TelegramMediaBot

A C#/.NET 8 Telegram bot that downloads TikTok and Instagram media — videos, reels, carousels, and slideshows.

**Instagram** runs a self-healing chain of extraction tiers — no account or cookies needed:

1. **Anonymous GraphQL** — Instagram's own logged-out web API. The `doc_id` it needs (which Instagram rotates every few weeks) is auto-discovered from Instagram's JS bundles and cached in `data/ig_docid.txt`.
2. **Public embed page** (`/embed/captioned/`) — a separate Instagram surface with its own rate limits.
3. **Embed-fixer services** (ddinstagram-style) — community-run resolvers, hosts configurable.
4. **Self-hosted [Cobalt](https://github.com/imputnet/cobalt)** — runs as a Docker sidecar on your own IP, auto-updated daily by Watchtower.
5. **Public Cobalt instances** — last resort.

Tiers that fail repeatedly are put on a short cooldown so they don't add latency; a **canary** re-tests every tier against a known post every 6 hours and (if `ADMIN_CHAT_ID` is set) alerts you on Telegram when tiers break.

**TikTok** uses yt-dlp for videos and gallery-dl for photo slideshows.
**ffmpeg** merges images + audio into slideshow videos when needed.

## Features

| Content type | How it works | Disk I/O |
|---|---|---|
| Instagram video / reel | extraction chain → CDN URL → Telegram `FromUri` | **None** |
| Instagram image | extraction chain → CDN URL → Telegram `FromUri` | **None** |
| Instagram carousel | extraction chain → CDN URLs → album `FromUri` | **None** |
| Instagram (URL send fails / local Cobalt) | downloaded to temp → re-uploaded | Temp files |
| TikTok video | yt-dlp pipes stdout → Telegram | **None** (streamed) |
| TikTok slideshow | gallery-dl images + audio → ffmpeg merge | Temp files |

**Group chats:** The bot auto-deletes the sender's message containing the link and sends just the media. Requires admin with "Delete Messages" permission.

## Project Structure

```
TelegramMediaBot/
├── Models/                 BotConfig, DownloadResult, YtDlpMeta
├── Services/               BotUpdateHandler, MediaDownloadService, InstagramService,
│   │                       IgCanaryService, YtDlpService, GalleryDlService, FfmpegService
│   └── Instagram/          Extraction tiers: GraphQlStrategy, EmbedPageStrategy,
│                           EmbedFixerStrategy, CobaltStrategy, DocIdProvider, TierHealthTracker
├── Helpers/                UrlHelper, FileTypeHelper, ProcessRunner
├── cookies/                optional cookies for yt-dlp/gallery-dl (gitignored)
├── tools_bin/              Windows only: yt-dlp.exe, ffmpeg.exe, gallery-dl.exe
├── data/                   temp/ (auto-cleaned every 30 min), ig_docid.txt cache
├── .github/workflows/
│   └── deploy.yml          GitHub Actions → EC2 (auto-setup + deploy)
├── Program.cs
├── appsettings.json        Non-sensitive config only
├── docker-compose.yml      bot + Cobalt sidecar + Watchtower
├── Dockerfile
└── .env.example            Template for secrets (gitignored when filled)
```

## Quick Start (Local Development)

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases)
- [ffmpeg](https://ffmpeg.org/download.html)
- [gallery-dl](https://github.com/mikf/gallery-dl) — `pip install gallery-dl`

### Windows

1. Download `yt-dlp.exe`, `ffmpeg.exe`, `gallery-dl.exe` into `tools_bin/`
2. Get a bot token from [@BotFather](https://t.me/BotFather)
3. Set environment variables:
   ```
   set Bot__Token=your-bot-token
   ```
4. `dotnet run`

### Linux

```bash
sudo apt install ffmpeg python3 python3-pip
pip install yt-dlp gallery-dl

export Bot__Token="your-bot-token"

dotnet run
```

## Configuration

Non-sensitive settings live in `appsettings.json`. **All secrets come from environment variables** — nothing sensitive is ever committed to git.

**Environment variables (secrets):**

| Variable | Description |
|---|---|
| `Bot__Token` | Telegram bot token (**required**) |
| `Bot__AdminChatId` | Chat id for canary alerts when IG extraction tiers break (optional) |
| `Bot__CobaltLocalUrl` | Self-hosted Cobalt URL (set automatically by docker-compose) |
| `Bot__IgDocId` | Manual override for the IG GraphQL doc_id (normally auto-discovered) |

**appsettings.json (non-sensitive defaults):**

| Setting | Default (Windows) | Default (Linux) |
|---|---|---|
| `YtDlpPath` | `tools_bin/yt-dlp.exe` | `yt-dlp` |
| `FfmpegPath` | `tools_bin/ffmpeg.exe` | `ffmpeg` |
| `GalleryDlPath` | `tools_bin/gallery-dl.exe` | `gallery-dl` |
| `CookiesFile` | `cookies/instagram_cookies.txt` (optional, yt-dlp/gallery-dl only) | same |
| `TempDir` | `data/temp` | same |
| `MaxFileSizeMb` | `50` | same |
| `SlideshowImageDurationSec` | `3` | same |

## Docker (Local)

```bash
cp .env.example .env
# Edit .env with your secrets
docker compose up --build -d
docker compose logs -f
```

## Deploy to AWS (EC2 + GitHub Actions)

The workflow handles **everything automatically** — first-time server setup, code deployment, and secret management. No manual SSH needed.

### 1. Launch an EC2 instance

In the AWS Console:
- **AMI:** Ubuntu 24.04 LTS
- **Type:** t3.micro (free tier) fits bot + Cobalt sidecar + Watchtower but is snug — add 1 GB of swap, or use t3.small for headroom
- **Storage:** 20 GB gp3
- **Security group:** outbound all, inbound SSH only (port 22)
- Create a key pair and download the `.pem` file

### 2. Add GitHub Secrets

Go to your repo → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**:

| Secret | Value |
|---|---|
| `EC2_HOST` | EC2 public IP address |
| `EC2_USER` | `ubuntu` |
| `EC2_SSH_KEY` | Full contents of your `.pem` key file |
| `BOT_TOKEN` | Telegram bot token from @BotFather |
| `ADMIN_CHAT_ID` | Your Telegram chat id for canary alerts (optional — get it from @userinfobot) |

Secrets are passed as environment variables to the SSH session — they never appear in workflow logs or script text.

### 3. Push to master

```bash
git push origin master
```

**First push** — the workflow will:
1. SSH into the EC2 instance
2. Install Docker, docker-compose, and git
3. Clone the repo
4. Write `.env` from secrets
5. Build the Docker image and start the containers (bot + Cobalt + Watchtower)

**Subsequent pushes** skip install/clone and just pull + rebuild (~2 minutes).

You can also trigger a deploy manually: **Actions** tab → **Deploy to AWS** → **Run workflow**.

### Viewing Logs

Logs are in Docker on the EC2 instance (not CloudWatch):

```bash
ssh -i key.pem ubuntu@your-ec2-ip
cd ~/TelegramMediaBot
docker compose logs --tail 100
docker compose logs -f  # live follow
```

Logs are capped at 10 MB (3 rotated files) to prevent disk fill.

### Maintenance

```bash
# Update yt-dlp inside container
docker compose exec bot yt-dlp -U

# Restart
docker compose restart

# Full rebuild
docker compose up --build -d --force-recreate
docker image prune -f
```

## Security

- **No secrets in git** — all sensitive values come from GitHub Secrets → env vars → `.env` on EC2
- **`.env` and cookies are gitignored** — never committed
- **Secrets are passed as SSH env vars** — not inlined in workflow script text, can't leak in logs
- **Workflow runs are safe to be public** — GitHub masks secret values with `***`

## Notes

- **Group chats:** Bot needs admin with "Delete Messages" permission. Disable group privacy in @BotFather.
- **yt-dlp updates:** TikTok/Instagram change APIs often — run `yt-dlp -U` periodically
- **Rate limiting:** Max 2 concurrent downloads per chat
- **Telegram file limit:** Videos over 50 MB sent as documents
- **Temp cleanup:** Orphaned temp files auto-cleaned every 30 minutes
