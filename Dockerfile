FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app

# Install external tools
RUN apt-get update && \
    apt-get install -y --no-install-recommends ffmpeg python3 python3-pip curl && \
    curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && \
    chmod a+rx /usr/local/bin/yt-dlp && \
    pip3 install --break-system-packages gallery-dl instagrapi requests && \
    ln -sf /usr/bin/python3 /usr/bin/python && \
    apt-get clean && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# Copy scripts (not included in dotnet publish output)
COPY scripts/ ./scripts/

# Create runtime directories
RUN mkdir -p data/temp cookies

ENTRYPOINT ["dotnet", "TelegramMediaBot.dll"]
