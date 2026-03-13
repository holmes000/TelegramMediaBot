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

**Group chats:** The bot auto-deletes the sender's message containing the link and replies with just the media (requires admin + delete messages permission).

## Project Structure

```
TelegramMediaBot/
├── Models/                 BotConfig, DownloadResult, YtDlpMeta
├── Services/               BotUpdateHandler, MediaDownloadService, InstagramService,
│                           YtDlpService, GalleryDlService, FfmpegService
├── Helpers/                UrlHelper, FileTypeHelper, ProcessRunner
├── scripts/
│   └── ig_media.py         Instagram private API (instagrapi) — extracts all media + audio
├── cookies/                instagram_cookies.txt (gitignored)
├── tools_bin/              Windows only: yt-dlp.exe, ffmpeg.exe, gallery-dl.exe
├── data/                   temp/ (auto-cleaned), ig_session.json (gitignored)
├── .github/workflows/      deploy.yml (GitHub Actions → EC2)
├── Program.cs
├── appsettings.json        Non-sensitive config only
├── docker-compose.yml
├── Dockerfile
└── setup-ec2.sh            One-time EC2 setup script
```

## Quick Start

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

Non-sensitive settings live in `appsettings.json`. **All secrets come from environment variables** (never committed to git).

**Environment variables (secrets):**

| Variable | Description |
|---|---|
| `Bot__Token` | Telegram bot token (**required**) |
| `Bot__InstagramSessionId` | Instagram sessionid cookie (for all IG content) |
| `Bot__InstagramUsername` | Alternative: IG username (triggers 2FA) |
| `Bot__InstagramPassword` | Alternative: IG password |

**appsettings.json (non-sensitive):**

| Setting | Default (Windows) | Default (Linux) | Description |
|---|---|---|---|
| `YtDlpPath` | `tools_bin/yt-dlp.exe` | `yt-dlp` | Path to yt-dlp |
| `FfmpegPath` | `tools_bin/ffmpeg.exe` | `ffmpeg` | Path to ffmpeg |
| `GalleryDlPath` | `tools_bin/gallery-dl.exe` | `gallery-dl` | Path to gallery-dl |
| `PythonPath` | `python` | `python3` | Path to Python |
| `CookiesFile` | `cookies/instagram_cookies.txt` | same | Netscape cookies file |
| `TempDir` | `data/temp` | same | Working directory |
| `MaxFileSizeMb` | `50` | same | Max video size before sending as document |
| `SlideshowImageDurationSec` | `3` | same | Seconds per image in slideshows |

## Docker

```bash
docker compose up --build -d
```

Secrets go in `.env` (copy from `.env.example`, gitignored):

```
BOT_TOKEN=your-token
INSTAGRAM_SESSION_ID=your-sessionid
```

## Deploy to AWS (EC2 + GitHub Actions)

### One-time EC2 setup

```bash
# 1. Launch EC2 instance
#    AMI: Ubuntu 24.04 LTS
#    Type: t3.small (2GB RAM for ffmpeg)
#    Security group: outbound all, inbound SSH only
#    Storage: 20 GB gp3

# 2. SSH in and run setup
ssh -i key.pem ubuntu@your-ip
# Run setup-ec2.sh or manually:
sudo apt update && sudo apt install -y docker.io docker-compose-v2 git
sudo usermod -aG docker $USER
# Log out and back in

# 3. Clone repo
git clone git@github.com:YOUR_USERNAME/TelegramMediaBot.git ~/TelegramMediaBot
cd ~/TelegramMediaBot

# 4. Configure secrets
cp .env.example .env
nano .env  # Add BOT_TOKEN and INSTAGRAM_SESSION_ID

# 5. Upload cookies
# From your local machine:
scp cookies/instagram_cookies.txt ubuntu@your-ip:~/TelegramMediaBot/cookies/

# 6. First deploy
docker compose up --build -d
docker compose logs -f
```

### GitHub Actions auto-deploy

Every push to `main` auto-deploys. Add these GitHub Secrets (repo → Settings → Secrets → Actions):

| Secret | Value |
|---|---|
| `EC2_HOST` | EC2 public IP |
| `EC2_USER` | `ubuntu` |
| `EC2_SSH_KEY` | Contents of your `.pem` key file |
| `BOT_TOKEN` | Telegram bot token |
| `INSTAGRAM_SESSION_ID` | Instagram sessionid |
| `INSTAGRAM_COOKIES` | Full contents of cookies.txt |

Then just:

```bash
git push origin main
# GitHub Actions SSHes into EC2, pulls, rebuilds, restarts. ~2 minutes.
```

### Maintenance

```bash
# View logs
docker compose logs --tail 100

# Restart
docker compose restart

# Update yt-dlp inside container
docker compose exec bot yt-dlp -U

# Manual redeploy
docker compose up --build -d --force-recreate
docker image prune -f
```

## Updating Instagram Session

The `InstagramSessionId` expires after a few months. To update:

1. Open Instagram in browser → DevTools → Application → Cookies → `sessionid`
2. Update the `INSTAGRAM_SESSION_ID` GitHub Secret
3. Push any commit (or re-run the deploy workflow)

## Notes

- **Group chats:** Bot needs admin rights with "Delete Messages" permission. Disable group privacy mode in @BotFather.
- **yt-dlp updates:** TikTok/Instagram change APIs often — run `yt-dlp -U` periodically
- **Rate limiting:** Max 2 concurrent downloads per chat
- **Telegram file limit:** Videos over 50 MB sent as documents
- **Temp cleanup:** Orphaned temp files auto-cleaned every 30 minutes
