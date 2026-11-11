# Alternating-batch live Toke count (the cross-check to the kernel trace). The bench runs inside a stand-in started like
# WindowsDaemonLaunch.Create starts h9kd (CREATE_NO_WINDOW console, NUL stdin, log as stdout/stderr) on a private desktop, so a start mode
# that makes a visible console window pops it there and not on the owner's screen. One pair = two batches of equal wall-clock length
# (launches, then idle to the end of the window); the arm that goes first swaps every pair.
#   pwsh -NoProfile -File bench-run.ps1 -Out C:\temp\h9kdetach\b1 -Name git-today-vs-null -A git:today -B git-null:today -Launches 50 -Window 70 -Pairs 3
param(
    [Parameter(Mandatory)] [string]$Out, [Parameter(Mandatory)] [string]$Name,
    [Parameter(Mandatory)] [string]$A, [Parameter(Mandatory)] [string]$B,
    [int]$Launches = 50, [double]$Window = 70, [int]$Pairs = 3, [string]$First = 'A',
    [string]$LauncherFlags = 'nowindow', [string]$Desktop = 'hidden',
    [string]$Probe = 'C:\temp\h9kdetach\bin\probe.exe'
)
New-Item -ItemType Directory -Force $Out | Out-Null
$log = Join-Path $Out "$Name.log"
Remove-Item $log -ErrorAction SilentlyContinue
$cmdArgs = @('standin', '--flags', $LauncherFlags, '--log', $log)
if ($Desktop -eq "hidden") { $cmdArgs += @('--desktop', "h9kbench$PID") }
$cmdArgs += @('--run', $Probe, 'bench', '--a', $A, '--b', $B, '--launches', $Launches, '--window', $Window, '--pairs', $Pairs, '--first', $First)
& $Probe @cmdArgs | Out-Null
Get-Content $log | Where-Object { $_ -match '^(pair|\d+,|TOTAL)' }
