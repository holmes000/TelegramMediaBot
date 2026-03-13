#!/bin/bash
# Run this ONCE on a fresh EC2 Ubuntu instance.
# Usage: ssh -i key.pem ubuntu@your-ip 'bash -s' < setup-ec2.sh

set -e

echo "=== Installing Docker ==="
sudo apt update
sudo apt install -y docker.io docker-compose-v2 git
sudo usermod -aG docker $USER

echo "=== Cloning repo ==="
# Replace with your repo URL
git clone git@github.com:YOUR_USERNAME/TelegramMediaBot.git ~/TelegramMediaBot
cd ~/TelegramMediaBot

echo "=== Creating .env ==="
cp .env.example .env
echo ""
echo "⚠️  Edit .env with your secrets:"
echo "    nano ~/TelegramMediaBot/.env"
echo ""

echo "=== Setting up cookies ==="
mkdir -p cookies data/temp
echo "⚠️  Upload your cookies file:"
echo "    scp cookies/instagram_cookies.txt ubuntu@this-ip:~/TelegramMediaBot/cookies/"
echo ""

echo "=== First deploy ==="
echo "After editing .env and uploading cookies, run:"
echo "    cd ~/TelegramMediaBot && docker compose up --build -d"
echo ""
echo "=== GitHub Actions setup ==="
echo "Add these secrets to your GitHub repo (Settings → Secrets → Actions):"
echo "  EC2_HOST     = $(curl -s http://169.254.169.254/latest/meta-data/public-ipv4 2>/dev/null || echo 'your-ec2-public-ip')"
echo "  EC2_USER     = ubuntu"
echo "  EC2_SSH_KEY  = (paste contents of your .pem key file)"
echo ""
echo "Then every push to main auto-deploys. ✅"
