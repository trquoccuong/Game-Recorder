Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$addonName = 'OdxCameraSpike'
$exeName = 'ArmaReforgerSteam.exe'
$gameGuess = if (Test-Path (Join-Path $PSScriptRoot $exeName)) { $PSScriptRoot } else { 'D:\Arma-Reforger-Local\Arma Reforger' }

# Next to the launcher when copied into the game folder; ..\addon when run from the repo.
function Find-AddonSource {
    foreach ($p in @((Join-Path $PSScriptRoot $addonName), (Join-Path $PSScriptRoot "..\addon\$addonName"))) {
        if (Test-Path (Join-Path $p "$addonName.gproj")) { return (Resolve-Path $p).Path }
    }
}

$form = New-Object Windows.Forms.Form
$form.Text = 'Odx Reforger Launcher'
$form.ClientSize = [Drawing.Size]::new(520, 245)
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.StartPosition = 'CenterScreen'

function Add-Label($text, $y) {
    $l = New-Object Windows.Forms.Label
    $l.Text = $text
    $l.Location = [Drawing.Point]::new(12, $y + 3)
    $l.AutoSize = $true
    $form.Controls.Add($l)
}

function Add-TextBox($y, $text, $width = 310) {
    $t = New-Object Windows.Forms.TextBox
    $t.Text = $text
    $t.Location = [Drawing.Point]::new(110, $y)
    $t.Width = $width
    $form.Controls.Add($t)
    $t
}

function Add-BrowseButton($y, $textBox) {
    $b = New-Object Windows.Forms.Button
    $b.Text = 'Browse...'
    $b.Location = [Drawing.Point]::new(428, $y - 1)
    $b.Width = 80
    $b.add_Click({
        $d = New-Object Windows.Forms.FolderBrowserDialog
        $d.SelectedPath = $textBox.Text
        if ($d.ShowDialog() -eq 'OK') { $textBox.Text = $d.SelectedPath }
    }.GetNewClosure())
    $form.Controls.Add($b)
}

Add-Label 'Game folder' 15
$tGame = Add-TextBox 15 $gameGuess
Add-BrowseButton 15 $tGame

Add-Label 'Server' 50
$tServer = Add-TextBox 50 '10.147.20.243:2001' 150

Add-Label 'Display' 85
$rWindow = New-Object Windows.Forms.RadioButton
$rWindow.Text = 'Window'
$rWindow.Location = [Drawing.Point]::new(110, 84)
$rWindow.AutoSize = $true
$rWindow.Checked = $true
$rFull = New-Object Windows.Forms.RadioButton
$rFull.Text = 'Fullscreen'
$rFull.Location = [Drawing.Point]::new(200, 84)
$rFull.AutoSize = $true
$form.Controls.AddRange(@($rWindow, $rFull))

Add-Label 'Password' 120
$tPass = Add-TextBox 120 '' 150
$tPass.UseSystemPasswordChar = $true

Add-Label 'Profile folder' 155
$tProfile = Add-TextBox 155 'D:\odx-dev\reforger-profile'
Add-BrowseButton 155 $tProfile

$status = New-Object Windows.Forms.Label
$status.Location = [Drawing.Point]::new(12, 205)
$status.Size = [Drawing.Size]::new(400, 30)
$form.Controls.Add($status)

$bLaunch = New-Object Windows.Forms.Button
$bLaunch.Text = 'Launch'
$bLaunch.Location = [Drawing.Point]::new(428, 200)
$bLaunch.Size = [Drawing.Size]::new(80, 30)
$form.Controls.Add($bLaunch)
$form.AcceptButton = $bLaunch

function Fail($message) {
    $status.ForeColor = 'Firebrick'
    $status.Text = $message
}

$launch = {
    $game = $tGame.Text.Trim()
    $exe = Join-Path $game $exeName
    if (-not (Test-Path $exe)) { return Fail "$exeName not found in the game folder." }

    $server = $tServer.Text.Trim()
    if ($server -notmatch '^[\w.-]+:\d+$') { return Fail 'Server must be ip:port, e.g. 10.147.20.243:2001.' }

    $installed = Join-Path $game "addons\$addonName"
    $src = Find-AddonSource
    if ($src) {
        robocopy $src $installed /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -ge 8) { return Fail "Copying $addonName into addons failed (robocopy $LASTEXITCODE)." }
    }
    if (-not (Test-Path (Join-Path $installed "$addonName.gproj"))) {
        return Fail "$addonName not found: put the addon folder next to this launcher."
    }

    $argList = @()
    if ($rWindow.Checked) { $argList += '-window' }
    $argList += '-addonsDir', "`"$game\addons`"", '-addons', $addonName, '-profile', "`"$($tProfile.Text.Trim())`"", '-client', $server
    # -password as a client parameter is unverified; a command-line (-server) server has no password anyway.
    if ($tPass.Text) { $argList += '-password', "`"$($tPass.Text)`"" }

    Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList ($argList -join ' ')
    $form.Close()
}
$bLaunch.add_Click($launch)

[void]$form.ShowDialog()
