# TelegramMediaBot

A C#/.NET 8 Telegram bot that downloads TikTok and Instagram media — videos, slideshows, carousels, stories, and image-with-music posts — using **yt-dlp**, **gallery-dl**, **ffmpeg**, and the **Instagram private API**.

## Features

| Content type | How it works | Disk I/O |
|---|---|---|
| TikTok / IG video | yt-dlp pipes stdout → Telegram | **None** (streamed) |
| IG image post (no music) | gallery-dl extracts CDN URLs → Telegram `FromUri` | **None** |
| IG image carousel (no music) | gallery-dl CDN URLs → album `FromUri` | **None** |
| TikTok slideshow (images + audio) | gallery-dl download → ffmpeg merge → send file | Temp files |
| IG image + music | gallery-dl image + instagrapi audio → ffmpeg merge | Temp files |
| IG mixed carousel + music | gallery-dl all + instagrapi audio → ffmpeg slideshow → album | Temp files |
| IG mixed carousel (no music) | gallery-dl all → send as mixed album | Temp files |

## Project Structure

```
TelegramMediaBot/
├── Models/                 Data models (BotConfig, DownloadResult, YtDlpMeta)
├── Services/               Tool wrappers (YtDlp, Ffmpeg, GalleryDl, InstagramAudio, MediaDownload)
├── Helpers/                Utilities (UrlHelper, FileTypeHelper, ProcessRunner)
├── scripts/
│   └── ig_audio.py         Python script for Instagram private API audio extraction
├── cookies/
│   └── instagram_cookies.txt   ← Place your cookies here
├── tools_bin/              Windows only: portable .exe files (yt-dlp, ffmpeg, gallery-dl)
├── data/
│   ├── temp/               Auto-created working directory (cleaned after each job)
│   └── ig_session.json     Persisted instagrapi session
├── BotUpdateHandler.cs     Telegram message handling
├── Program.cs              Entry point + DI setup
├── appsettings.json        Configuration
├── Dockerfile              Production container
└── TelegramMediaBot.csproj
```

## Quick Start

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases)
- [ffmpeg](https://ffmpeg.org/download.html)
- [gallery-dl](https://github.com/mikf/gallery-dl) — `pip install gallery-dl`
- [instagrapi](https://github.com/subzeroid/instagrapi) — `pip install instagrapi requests`

### Windows Setup

1. Download `yt-dlp.exe`, `ffmpeg.exe`, `gallery-dl.exe` into `tools_bin/`
2. Install Python packages: `pip install gallery-dl instagrapi requests`
3. Get a bot token from [@BotFather](https://t.me/BotFather)
4. Edit `appsettings.json` — set `Token`
5. Export Instagram cookies → save as `cookies/instagram_cookies.txt`
6. (Optional) Set `InstagramSessionId` for image-with-music audio support
7. `dotnet run`

### Linux Setup

```bash
# Install tools (Ubuntu/Debian)
sudo apt install ffmpeg python3 python3-pip
pip install yt-dlp gallery-dl instagrapi requests

# Configure
cp appsettings.json appsettings.json.bak
# Edit appsettings.json — set Token, InstagramSessionId, etc.

# Run
dotnet run
```

## Configuration

All settings are in `appsettings.json` under the `"Bot"` section.
Every setting can be overridden via environment variables: `Bot__Token`, `Bot__CookiesFile`, etc.

| Setting | Default (Windows) | Default (Linux) | Description |
|---|---|---|---|
| `Token` | — | — | Telegram bot token (**required**) |
| `YtDlpPath` | `tools_bin/yt-dlp.exe` | `yt-dlp` | Path to yt-dlp |
| `FfmpegPath` | `tools_bin/ffmpeg.exe` | `ffmpeg` | Path to ffmpeg |
| `GalleryDlPath` | `tools_bin/gallery-dl.exe` | `gallery-dl` | Path to gallery-dl |
| `PythonPath` | `tools_bin/python.exe` | `python` | Path to Python |
| `CookiesFile` | `cookies/instagram_cookies.txt` | same | Netscape cookies file |
| `InstagramSessionId` | — | — | IG sessionid cookie (for image+music) |
| `TempDir` | `data/temp` | same | Working directory for downloads |
| `MaxFileSizeMb` | `50` | same | Max video size before sending as document |
| `SlideshowImageDurationSec` | `3` | same | Seconds per image in slideshows |

Leave tool paths empty (`""`) in appsettings.json to use platform defaults.

## Docker

```bash
docker build -t media-bot .

docker run -d \
  --name media-bot \
  -e Bot__Token="your-token" \
  -e Bot__InstagramSessionId="your-sessionid" \
  -v ./cookies:/app/cookies \
  -v ./data:/app/data \
  media-bot
```

---

## AWS Deployment

### Option A: EC2 with Docker (simplest)

Best for: getting started, full control, low cost (~$5-10/mo with t3.micro).

```bash
# 1. Launch EC2 instance
#    - AMI: Ubuntu 24.04 LTS
#    - Instance type: t3.micro (free tier) or t3.small
#    - Security group: outbound all (no inbound needed — bot uses polling)
#    - Storage: 20 GB gp3

# 2. SSH in and install Docker
sudo apt update && sudo apt install -y docker.io docker-compose-v2
sudo usermod -aG docker $USER
# Log out and back in

# 3. Clone/upload your project
scp -r TelegramMediaBot/ ec2-user@your-ip:~/

# 4. Create docker-compose.yml
cat > docker-compose.yml << 'EOF'
services:
  bot:
    build: .
    restart: unless-stopped
    environment:
      - Bot__Token=your-telegram-bot-token
      - Bot__InstagramSessionId=your-session-id
    volumes:
      - ./cookies:/app/cookies
      - ./data:/app/data
EOF

# 5. Deploy
cd TelegramMediaBot
docker compose up -d --build

# 6. Check logs
docker compose logs -f
```

**Maintenance:**
```bash
# Update yt-dlp (inside container)
docker compose exec bot yt-dlp -U

# View logs
docker compose logs --tail 100

# Restart
docker compose restart

# Redeploy after code changes
docker compose up -d --build
```

### Option B: ECS Fargate (serverless containers)

Best for: hands-off operation, auto-restart, no EC2 management.

```bash
# 1. Install AWS CLI + configure credentials
aws configure

# 2. Create ECR repository
aws ecr create-repository --repository-name media-bot --region us-east-1

# 3. Build and push Docker image
aws ecr get-login-password --region us-east-1 | docker login --username AWS --password-stdin <account-id>.dkr.ecr.us-east-1.amazonaws.com
docker build -t media-bot .
docker tag media-bot:latest <account-id>.dkr.ecr.us-east-1.amazonaws.com/media-bot:latest
docker push <account-id>.dkr.ecr.us-east-1.amazonaws.com/media-bot:latest

# 4. Create ECS cluster + task definition + service
#    Use the AWS Console or Copilot CLI for easier setup:
#    https://aws.github.io/copilot-cli/

# Alternatively, use AWS Copilot (one-command deploy):
brew install aws/tap/copilot-cli   # or snap install copilot-cli
copilot init \
  --app media-bot \
  --name bot \
  --type "Backend Service" \
  --dockerfile ./Dockerfile

copilot env init --name prod --profile default
copilot deploy --name bot --env prod
```

**ECS Task Definition essentials:**
- CPU: 256 (0.25 vCPU), Memory: 512 MB
- No port mappings needed (bot uses polling, not webhooks)
- Environment variables: `Bot__Token`, `Bot__InstagramSessionId`
- EFS mount for `/app/data` and `/app/cookies` (persistent storage)

### Option C: Lightsail Container (simplest AWS option)

```bash
# 1. Install Lightsail CLI plugin
aws lightsail create-container-service \
  --service-name media-bot \
  --power micro \
  --scale 1

# 2. Push image
aws lightsail push-container-image \
  --service-name media-bot \
  --label latest \
  --image media-bot:latest

# 3. Deploy with environment variables via Lightsail console
```

### Cost Comparison

| Option | Monthly Cost | Pros | Cons |
|---|---|---|---|
| EC2 t3.micro | ~$8 | Full control, persistent storage | Manual updates |
| EC2 t3.small | ~$15 | More headroom for ffmpeg | Manual updates |
| ECS Fargate | ~$10-15 | Auto-restart, no server mgmt | Complex setup, EFS cost |
| Lightsail | ~$7 | Simplest AWS option | Limited customization |

### Recommended: EC2 t3.small + Docker Compose

For a Telegram bot that does video processing (ffmpeg), `t3.small` (2 GB RAM) gives enough headroom.
The bot uses polling (not webhooks), so no inbound ports or load balancers are needed — just outbound internet access.

---

## Updating Instagram Session

When the `InstagramSessionId` expires (typically after a few months):

1. Open Instagram in your browser → Developer Tools → Application → Cookies
2. Copy the value of the `sessionid` cookie
3. Update `Bot__InstagramSessionId` in your config or environment variable
4. Restart the bot

## Notes

- **Group chats**: Disable group privacy mode in @BotFather for the bot to see all messages
- **yt-dlp updates**: Run `yt-dlp -U` periodically — TikTok/Instagram change their APIs often
- **gallery-dl updates**: `pip install -U gallery-dl`
- **Rate limiting**: The bot limits to 2 concurrent downloads per chat to prevent abuse
- **Telegram file limit**: Videos over 50 MB are sent as documents
