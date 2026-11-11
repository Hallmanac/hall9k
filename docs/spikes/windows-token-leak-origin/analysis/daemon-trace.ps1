# Kernel trace of the running h9kd until one gate run and one agent session start have been seen.
# Elevated. Does not stop, restart or touch the daemon: it only reads Win32_Process and runs wpr.
#   pwsh -NoProfile -File daemon-trace.ps1 -Out C:\temp\tokeleak -Profile toke-nostacks.wprp -DaemonPid 34684 [-MaxMinutes 60]
param(
    [Parameter(Mandatory)] [string]$Out,
    [Parameter(Mandatory)] [string]$Profile,
    [Parameter(Mandatory)] [int]$DaemonPid,
    [string]$Name = 'daemon',
    [int]$MaxMinutes = 60,
    [int]$TailSeconds = 90          # wait after the last event of interest so its tokens had time to be freed
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $Out "$Name-watch.log"
function Log([string]$m) { "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m | Tee-Object -FilePath $log -Append }
$d = Get-Process -Id $DaemonPid
Log ("h9kd pid {0} started {1:o} path {2}" -f $d.Id, $d.StartTime, $d.Path)
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
$t0 = Get-Date; Log "trace started"
$seen = @{}; $claude = $null; $gate = $null; $doneAt = $null
# sessions and gates that were already running when the trace began are not "starts" inside it
foreach ($c in Get-CimInstance Win32_Process -Filter "ParentProcessId=$DaemonPid") { $seen[$c.ProcessId] = $true; Log ("already running at trace start: pid {0} {1}" -f $c.ProcessId, $c.Name) }
while (((Get-Date) - $t0).TotalMinutes -lt $MaxMinutes) {
    foreach ($c in Get-CimInstance Win32_Process -Filter "ParentProcessId=$DaemonPid" -ErrorAction SilentlyContinue) {
        if ($c.Name -ne 'cmd.exe' -or $seen.ContainsKey($c.ProcessId)) { continue }
        $seen[$c.ProcessId] = $true
        $cl = $c.CommandLine
        if ($cl -match '"claude"') { $kind = 'agent-session' } else { $kind = 'gate-or-other' }
        Log ("new cmd.exe child of h9kd: pid {0} kind {1}: {2}" -f $c.ProcessId, $kind, $cl.Substring(0, [Math]::Min(200, $cl.Length)))
        if ($kind -eq 'agent-session' -and -not $claude) { $claude = $c.ProcessId }
        if ($kind -eq 'gate-or-other' -and -not $gate) { $gate = $c.ProcessId }
    }
    if ($claude -and $gate) {
        $gateAlive = [bool](Get-Process -Id $gate -ErrorAction SilentlyContinue)
        if (-not $doneAt) { $doneAt = Get-Date; Log "both kinds seen (agent $claude, gate $gate)" }
        if (-not $gateAlive) { Log "gate cmd.exe $gate has exited"; break }
        if (((Get-Date) - $doneAt).TotalMinutes -gt 20) { Log "gate $gate still running 20 minutes after both were seen; stopping anyway"; break }
    }
    Start-Sleep -Seconds 2
}
Log "waiting $TailSeconds s before stopping the trace"
Start-Sleep -Seconds $TailSeconds
wpr -stop $etl 2>&1 | Select-Object -Last 1
Log ("trace stopped after {0:N0} s; h9kd still pid {1}: {2}" -f ((Get-Date) - $t0).TotalSeconds, $DaemonPid, [bool](Get-Process -Id $DaemonPid -ErrorAction SilentlyContinue))
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
Log "converted to CSV"
