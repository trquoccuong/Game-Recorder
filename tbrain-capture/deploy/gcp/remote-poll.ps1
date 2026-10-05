# Every 30 s: runs gs://tbrain-game-server-setup/run-once.ps1 when it appears (output to logs/run-once-<time>.txt), and
# every 5 min mirrors the sessions (C:\tbrain\odx\data\delivery) to gs://tbrain-game-server-setup/delivery/.
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
    if ($n % 10 -eq 0 -and (Test-Path C:\tbrain\odx\data\delivery)) {
        gcloud storage rsync -r C:\tbrain\odx\data\delivery gs://tbrain-game-server-setup/delivery --quiet *> C:\tbrain\logs\delivery-sync.txt
    }
    $n++
    Start-Sleep 30
}
