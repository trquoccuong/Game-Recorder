#!/usr/bin/env bash
# Starts (or updates) odx-orchestrator on this server: bash up.sh
# Needs Docker with the compose plugin, the domain's DNS pointing here, and ports 80/tcp, 443/tcp+udp, 12300/udp and
# 9993/udp (ZeroTier, rooms with a LAN) open.
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
  cp .env.example .env
  key=$(head -c 24 /dev/urandom | base64 | tr -d '/+=')
  sed -i "s/^ODX_ACCESS_KEY=.*/ODX_ACCESS_KEY=$key/" .env
  echo "Created .env with a new access key. Set ODX_DOMAIN in it, then run: bash up.sh"
  exit 1
fi
set -a; . ./.env; set +a
if [ "$ODX_DOMAIN" = "capture.example.com" ] || [ "$ODX_ACCESS_KEY" = "change-me" ]; then
  echo "Set ODX_DOMAIN (and ODX_ACCESS_KEY) in .env first."
  exit 1
fi


# The ZeroTier controller's API token: written before its first start (zerotier-one keeps an existing one) and handed to
# the orchestrator through .env.
mkdir -p zerotier
if ! grep -q '^ODX_ZT_TOKEN=.' .env; then
  ztkey=$(head -c 24 /dev/urandom | base64 | tr -d '/+=')
  sed -i '/^ODX_ZT_TOKEN=/d' .env && echo "ODX_ZT_TOKEN=$ztkey" >> .env
  echo -n "$ztkey" | sudo tee zerotier/authtoken.secret > /dev/null && sudo chmod 600 zerotier/authtoken.secret
  export ODX_ZT_TOKEN=$ztkey
fi
docker compose up -d --build

echo
echo "Orchestrator: https://$ODX_DOMAIN  (time service: $ODX_DOMAIN:12300/udp)"
echo "Package odx-app for it on Windows:"
echo "  powershell -ExecutionPolicy Bypass -File publish.ps1 -Orchestrator https://$ODX_DOMAIN -AccessKey $ODX_ACCESS_KEY"
echo "On the CS2 server's machine (docs/cs2-server.md):"
echo "  bin\\odx.exe game-server --orchestrator https://$ODX_DOMAIN --cs2-game-dir <cs2>\\game\\csgo --cs2-rcon-password-file <file>"
