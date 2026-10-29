# Builds the mod, then starts a host and a client of the game side by side on this machine, joined
# over the local network. The host continues its save as soon as the client is connected. Running
# instances of the game are closed first.
#
#   powershell -ExecutionPolicy Bypass -File tools\test-lan.ps1 [-GameDir DIR] [-NoBuild]
param(
    [string]$GameDir,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# The build already knows where the game is.
if (-not $GameDir) {
    $props = Join-Path $repo 'Directory.Build.local.props'
    if ((Test-Path $props) -and ((Get-Content $props -Raw) -match '<GameDir>\s*([^<]+?)\s*</GameDir>')) {
        $GameDir = $Matches[1]
    } else {
        $GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\My Summer Car'
    }
}
$exe = Join-Path $GameDir 'mysummercar.exe'
if (-not (Test-Path $exe)) {
    throw "The game is not in $GameDir, pass -GameDir."
}

$running = @(Get-Process mysummercar -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host 'Closing the running game.'
    $running | Stop-Process -Force
    $running | ForEach-Object { $_.WaitForExit(30000) | Out-Null }
}

if (-not $NoBuild) {
    dotnet build (Join-Path $repo 'MSCCoop.slnx') -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace MSCCoopTest -Name Window -MemberDefinition @'
[DllImport("user32.dll")]
public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
'@

# Two 16:9 windows filling the screen's width, with room for their frames.
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$width = [int]($area.Width / 2) - 16
$height = [Math]::Min([int]($width * 9 / 16), $area.Height - 40)

function Start-Game([string]$log, [string[]]$extra) {
    Remove-Item $log -ErrorAction SilentlyContinue
    $arguments = @('-mscloader-sipatch', '-screen-fullscreen', '0', '-screen-width', $width, '-screen-height', $height,
        '-logFile', "`"$log`"") + $extra
    Start-Process $exe -ArgumentList $arguments -WorkingDirectory $GameDir -PassThru
}

# The mod writes this line once the game is in the main menu and the session is up.
function Wait-Log([System.Diagnostics.Process]$game, [string]$log, [string]$line) {
    $deadline = (Get-Date).AddMinutes(3)
    while (-not ((Test-Path $log) -and (Select-String -Path $log -Pattern $line -SimpleMatch -Quiet -ErrorAction SilentlyContinue))) {
        if ($game.HasExited) {
            throw "The game closed while starting, see $log."
        }
        if ((Get-Date) -gt $deadline) {
            throw "'$line' never showed up in $log."
        }
        Start-Sleep -Milliseconds 500
    }
}

function Move-Window([System.Diagnostics.Process]$game, [int]$x) {
    $game.Refresh()
    [MSCCoopTest.Window]::SetWindowPos($game.MainWindowHandle, [IntPtr]::Zero, $x, $area.Top, 0, 0, 0x0005) | Out-Null
}

$hostLog = Join-Path $GameDir 'output_log.txt'
$clientLog = Join-Path $GameDir 'output_log_client.txt'

Write-Host 'Starting the host.'
$hostGame = Start-Game $hostLog @('-coop-lan-host', '-coop-autostart')
Wait-Log $hostGame $hostLog 'Co-op: hosting'
Move-Window $hostGame $area.Left

Write-Host 'Starting the client.'
$clientGame = Start-Game $clientLog @('-coop-lan-join', '127.0.0.1')
Wait-Log $clientGame $clientLog 'Co-op: connecting'
Move-Window $clientGame ($area.Left + [int]($area.Width / 2))

Write-Host 'Both are up. The host loads its save as soon as the client is connected.'
