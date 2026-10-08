# Every 30 s: runs gs://tbrain-game-server-setup/run-once.ps1 when it appears (output to logs/run-once-<time>.txt), and
# every 5 min mirrors the server's side of each round (C:\tbrain\game-server, odx game-server --work) to
# gs://tbrain-game-server-setup/game-server/, to be copied next to the players' folders for odx merge.
$n = 0
while ($true) {
    $stamp = Get-Date -Format yyyyMMdd-HHmmss
    gcloud storage cp gs://tbrain-game-server-setup/run-once.ps1 C:\tbrain\run-once.ps1 --quiet 2>$null
    if (Test-Path C:\tbrain\run-once.ps1) {
        gcloud storage rm gs://tbrain-game-server-setup/run-once.ps1 --quiet 2>$null
        & powershell -NoProfile -ExecutionPolicy Bypass -File C:\tbrain\run-once.ps1 *> C:\tbrain\logs\run-once.txt
        gcloud storage cp C:\tbrain\logs\run-once.txt "gs://tbrain-game-server-setup/logs/run-once-$stamp.txt" --quiet 2>$null
        Remove-Item C:\tbrain\run-once.ps1
    }
    if ($n % 10 -eq 0 -and (Test-Path C:\tbrain\game-server)) {
        gcloud storage rsync -r C:\tbrain\game-server gs://tbrain-game-server-setup/game-server --quiet *> C:\tbrain\logs\game-server-sync.txt
    }
    $n++
    Start-Sleep 30
}
