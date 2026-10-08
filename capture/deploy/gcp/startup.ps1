# Runs setup.ps1 and build-package.ps1 side by side (each until its done marker exists) and echoes their progress lines
# to the serial port.
New-Item -ItemType Directory -Force C:\odx\logs | Out-Null
# The logs go to the bucket at boot (before setup.out is overwritten) and every 30 s, to follow the setup from outside.
function Push-Logs($to) { gcloud storage cp 'C:\odx\logs\*' "gs://odx-game-server-setup/logs/$to/" --quiet 2>$null | Out-Null }
Push-Logs "boot-$(Get-Date -Format yyyyMMdd-HHmmss)"
$procs = @()
foreach ($s in @(@('setup', 'setup-done.txt'), @('build-package', 'package-done.txt'))) {
    if (Test-Path "C:\odx\$($s[1])") { continue }
    gcloud storage cp "gs://odx-game-server-setup/$($s[0]).ps1" "C:\odx\$($s[0]).ps1" --quiet
    $out = "C:\odx\logs\$($s[0]).out"
    $p = Start-Process powershell -PassThru -RedirectStandardOutput $out -RedirectStandardError "$out.err" `
        -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File C:\odx\$($s[0]).ps1"
    $null = $p.Handle
    $procs += @{ Name = $s[0]; Proc = $p; Out = $out; Seen = 0 }
}
function Echo-New {
    foreach ($x in $procs) {
        if (-not (Test-Path $x.Out)) { continue }
        $lines = @(Get-Content $x.Out -ErrorAction SilentlyContinue)
        if ($lines.Count -le $x.Seen) { continue }
        foreach ($l in $lines[$x.Seen..($lines.Count - 1)]) { if ($l -match 'odx-|progress:|error|Error') { Write-Host $l } }
        $x.Seen = $lines.Count
    }
}
while (@($procs | Where-Object { -not $_.Proc.HasExited }).Count) {
    Echo-New; Push-Logs 'current'
    # build-package waits for the access key setup.ps1 writes: stop it when setup.ps1 failed.
    $setup = $procs | Where-Object { $_.Name -eq 'setup' }
    if ($setup -and $setup.Proc.HasExited -and $setup.Proc.ExitCode) { $procs | ForEach-Object { if (-not $_.Proc.HasExited) { $_.Proc.Kill() } } }
    Start-Sleep 30
}
Echo-New
Push-Logs 'current'
foreach ($x in $procs) {
    if ($x.Proc.ExitCode) { Write-Host "odx-$($x.Name): FAILED exit $($x.Proc.ExitCode)"; Get-Content "$($x.Out).err" -Tail 20 | Write-Host }
}

# odx-remote: runs run-once.ps1 from the bucket and mirrors the server's rounds to it (remote-poll.ps1).
if (-not (Get-ScheduledTask odx-remote -ErrorAction SilentlyContinue)) {
    gcloud storage cp gs://odx-game-server-setup/remote-poll.ps1 C:\odx\remote-poll.ps1 --quiet 2>$null
    $action = New-ScheduledTaskAction -Execute powershell.exe -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\odx\remote-poll.ps1'
    $settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName odx-remote -Action $action -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings `
        -Principal (New-ScheduledTaskPrincipal -UserId SYSTEM -RunLevel Highest) -Force | Out-Null
    Start-ScheduledTask odx-remote
}
