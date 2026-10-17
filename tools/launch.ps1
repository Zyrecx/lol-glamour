# Starts the Glamour desktop app from a copy in %LOCALAPPDATA%\Glamour\app, rebuilding that copy first when the sources changed.
# Run through Glamour.vbs (repo root) so no console window shows. The copy keeps the repo's bin unlocked, so dotnet build works while Glamour runs.
# -Shortcut: also put a "Glamour" shortcut on the desktop that runs Glamour.vbs.
param([switch]$Shortcut)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$app = Join-Path $env:LOCALAPPDATA 'Glamour\app'
$exe = Join-Path $app 'Glamour.exe'
$stamp = Join-Path $app 'source-stamp.txt'
$buildLog = Join-Path $env:LOCALAPPDATA 'Glamour\logs\launch-build.log'
Add-Type -AssemblyName System.Windows.Forms

if ($Shortcut) {
    $lnk = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Glamour.lnk'))
    $lnk.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
    $lnk.Arguments = '"' + (Join-Path $repo 'Glamour.vbs') + '"'
    $lnk.WorkingDirectory = $repo
    if (Test-Path $exe) { $lnk.IconLocation = "$exe,0" }
    $lnk.Save()
}

# Already running: the exe itself just brings the open window forward (single instance). An update waits until it is exited from the tray.
if (Get-Process -Name Glamour -ErrorAction SilentlyContinue) { if (Test-Path $exe) { Start-Process $exe }; exit }

# Newest change to anything that goes into the app; the copy records the one it was built from.
$sources = @(
    Get-ChildItem (Join-Path $repo 'src') -Recurse -File -Include *.cs, *.csproj, *.json, *.gray | Where-Object { $_.FullName -notmatch '\\(bin|obj|wwwroot)\\' }
    Get-ChildItem (Join-Path $repo 'web') -File
    Get-ChildItem (Join-Path $repo 'web\src'), (Join-Path $repo 'web\public') -Recurse -File
)
$newest = ($sources | Sort-Object LastWriteTimeUtc | Select-Object -Last 1).LastWriteTimeUtc.Ticks.ToString()
$current = (Test-Path $exe) -and (Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $newest)

if (-not $current) {
    $note = New-Object System.Windows.Forms.NotifyIcon
    $note.Icon = [System.Drawing.SystemIcons]::Information
    $note.Visible = $true
    $note.ShowBalloonTip(5000, 'Glamour', 'Updating, this takes about a minute...', 'Info')
    New-Item -ItemType Directory -Force (Split-Path $buildLog) | Out-Null
    try {
        & {
            # Tools warn on stderr (Vite's PLUGIN_TIMINGS); 5.1 turns that into a throw under Stop. Exit codes decide.
            $ErrorActionPreference = 'Continue'
            $web = Join-Path $repo 'web'
            if (-not (Test-Path (Join-Path $web 'node_modules'))) { npm ci --prefix $web; if ($LASTEXITCODE) { throw 'npm ci failed' } }
            npm run build --prefix $web; if ($LASTEXITCODE) { throw 'UI build failed' }
            dotnet build (Join-Path $repo 'src\Glamour.Desktop') -c Release; if ($LASTEXITCODE) { throw 'dotnet build failed' }
        } *> $buildLog
        $out = Join-Path $repo 'src\Glamour.Desktop\bin\Release\net10.0-windows10.0.19041.0'
        if (Test-Path $app) { Remove-Item $app -Recurse -Force }
        Copy-Item $out $app -Recurse
        Set-Content $stamp $newest
    }
    catch {
        $note.Dispose()
        $tail = if (Test-Path $buildLog) { (Get-Content $buildLog -Tail 15) -join "`n" } else { '' }
        $msg = "Glamour could not be built: $($_.Exception.Message)`n`nFull output: $buildLog`n`n$tail"
        if (Test-Path $exe) { $msg += "`n`nStarting the last working version." }
        [System.Windows.Forms.MessageBox]::Show($msg, 'Glamour', 'OK', 'Error') | Out-Null
        if (-not (Test-Path $exe)) { exit 1 }
    }
    $note.Dispose()
}

Start-Process $exe
