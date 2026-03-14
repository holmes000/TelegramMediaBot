# TelegramMediaBot

A C#/.NET 8 Telegram bot that downloads TikTok and Instagram media — videos, slideshows, carousels, stories, and image-with-music posts.

**Instagram** uses the private (mobile) API via [instagrapi](https://github.com/subzeroid/instagrapi) — one API call gets everything.
**TikTok** uses yt-dlp for videos and gallery-dl for photo slideshows.
**ffmpeg** merges images + audio into slideshow videos when needed.

## Features

| Content type | How it works | Disk I/O |
|---|---|---|
| Instagram video / reel | instagrapi CDN URL → Telegram `FromUri` | **None** |
| Instagram image (no music) | instagrapi CDN URL → Telegram `FromUri` | **None** |
| Instagram carousel (no music) | instagrapi CDN URLs → album `FromUri` | **None** |
| Instagram image + music | instagrapi image + audio → ffmpeg merge | Temp files |
| Instagram carousel + music | instagrapi all + audio → ffmpeg slideshow + album | Temp files |
| TikTok video | yt-dlp pipes stdout → Telegram | **None** (streamed) |
| TikTok slideshow | gallery-dl images + audio → ffmpeg merge | Temp files |

**Group chats:** The bot auto-deletes the sender's message containing the link and sends just the media. Requires admin with "Delete Messages" permission.

## Project Structure

```
TelegramMediaBot/
├── Models/                 BotConfig, DownloadResult, YtDlpMeta
├── Services/               BotUpdateHandler, MediaDownloadService, InstagramService,
│                           YtDlpService, GalleryDlService, FfmpegService
├── Helpers/                UrlHelper, FileTypeHelper, ProcessRunner
├── scripts/
│   └── ig_media.py         Instagram private API — extracts all media + audio in one call
├── cookies/                instagram_cookies.txt (gitignored)
├── tools_bin/              Windows only: yt-dlp.exe, ffmpeg.exe, gallery-dl.exe
├── data/                   temp/ (auto-cleaned every 30 min), ig_session.json (gitignored)
├── .github/workflows/
│   └── deploy.yml          GitHub Actions → EC2 (auto-setup + deploy)
├── Program.cs
├── appsettings.json        Non-sensitive config only
├── docker-compose.yml
├── Dockerfile
└── .env.example            Template for secrets (gitignored when filled)
```

## Quick Start (Local Development)

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Python 3](https://www.python.org/) + `pip install instagrapi requests`
- [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases)
- [ffmpeg](https://ffmpeg.org/download.html)
- [gallery-dl](https://github.com/mikf/gallery-dl) — `pip install gallery-dl`

### Windows

1. Download `yt-dlp.exe`, `ffmpeg.exe`, `gallery-dl.exe` into `tools_bin/`
2. `pip install instagrapi requests gallery-dl`
3. Get a bot token from [@BotFather](https://t.me/BotFather)
4. Set environment variables:
   ```
   set Bot__Token=your-bot-token
   set Bot__InstagramSessionId=your-sessionid
   ```
5. Export Instagram cookies → save as `cookies/instagram_cookies.txt`
6. `dotnet run`

### Linux

```bash
sudo apt install ffmpeg python3 python3-pip
pip install yt-dlp gallery-dl instagrapi requests

export Bot__Token="your-bot-token"
export Bot__InstagramSessionId="your-sessionid"

dotnet run
```

## Configuration

Non-sensitive settings live in `appsettings.json`. **All secrets come from environment variables** — nothing sensitive is ever committed to git.

**Environment variables (secrets):**

| Variable | Description |
|---|---|
| `Bot__Token` | Telegram bot token (**required**) |
| `Bot__InstagramSessionId` | Instagram sessionid cookie (for all IG content) |
| `Bot__InstagramUsername` | Alternative: IG username |
| `Bot__InstagramPassword` | Alternative: IG password |

**appsettings.json (non-sensitive defaults):**

| Setting | Default (Windows) | Default (Linux) |
|---|---|---|
| `YtDlpPath` | `tools_bin/yt-dlp.exe` | `yt-dlp` |
| `FfmpegPath` | `tools_bin/ffmpeg.exe` | `ffmpeg` |
| `GalleryDlPath` | `tools_bin/gallery-dl.exe` | `gallery-dl` |
| `PythonPath` | `python` | `python3` |
| `CookiesFile` | `cookies/instagram_cookies.txt` | same |
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
- **Type:** t3.micro (free tier) or t3.small (more headroom for ffmpeg)
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
| `INSTAGRAM_SESSION_ID` | Instagram `sessionid` cookie value |
| `INSTAGRAM_COOKIES` | Full contents of your cookies.txt file |

Secrets are passed as environment variables to the SSH session — they never appear in workflow logs or script text.

### 3. Push to master

```bash
git push origin master
```

**First push** — the workflow will:
1. SSH into the EC2 instance
2. Install Docker, docker-compose, and git
3. Clone the repo
4. Write `.env` and `cookies/instagram_cookies.txt` from secrets
5. Build the Docker image and start the container

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

## Updating Instagram Session

The `InstagramSessionId` expires after a few months. To update:

1. Open Instagram in browser → DevTools → Application → Cookies → copy `sessionid` value
2. Update the `INSTAGRAM_SESSION_ID` secret in GitHub (repo → Settings → Secrets)
3. Push any commit or re-run the deploy workflow from the Actions tab

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
