#!/usr/bin/env bash
# Starts (or updates) odx-orchestrator on this server: bash up.sh
# Needs Docker with the compose plugin, the domain's DNS pointing here, and ports 80/tcp, 443/tcp+udp and 12300/udp open.
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

mkdir -p sessions
[ "$(stat -c %u sessions)" = "1654" ] || sudo chown 1654:1654 sessions  # the container's non-root user
docker compose up -d --build

echo
echo "Orchestrator: https://$ODX_DOMAIN  (time service: $ODX_DOMAIN:12300/udp)"
echo "Sessions:     $(pwd)/sessions"
echo "Package odx-app for it on Windows:"
echo "  powershell -ExecutionPolicy Bypass -File publish.ps1 -Orchestrator https://$ODX_DOMAIN -AccessKey $ODX_ACCESS_KEY"
