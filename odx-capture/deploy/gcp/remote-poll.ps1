# Every 30 s: runs gs://odx-game-server-setup/run-once.ps1 when it appears (output to logs/run-once-<time>.txt), and
# every 5 min mirrors the sessions (C:\odx\odx\data\delivery) to gs://odx-game-server-setup/delivery/.
$n = 0
while ($true) {
    $stamp = Get-Date -Format yyyyMMdd-HHmmss
    gcloud storage cp gs://odx-game-server-setup/run-once.ps1 C:\odx\run-once.ps1 --quiet 2>$null
    if (Test-Path C:\odx\run-once.ps1) {
        gcloud storage rm gs://odx-game-server-setup/run-once.ps1 --quiet 2>$null
        & powershell -NoProfile -ExecutionPolicy Bypass -File C:\odx\run-once.ps1 *> C:\odx\logs\run-once.txt
        gcloud storage cp C:\odx\logs\run-once.txt "gs://odx-game-server-setup/logs/run-once-$stamp.txt" --quiet 2>$null
        Remove-Item C:\odx\run-once.ps1
    }
    if ($n % 10 -eq 0 -and (Test-Path C:\odx\odx\data\delivery)) {
        gcloud storage rsync -r C:\odx\odx\data\delivery gs://odx-game-server-setup/delivery --quiet *> C:\odx\logs\delivery-sync.txt
    }
    $n++
    Start-Sleep 30
}
