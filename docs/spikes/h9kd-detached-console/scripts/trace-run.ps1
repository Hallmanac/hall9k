# One kernel pool trace holding several arms. Each arm is one `probe run` process started like WindowsDaemonLaunch.Create starts h9kd
# (CreateProcessW, NUL stdin, log as stdout/stderr, CREATE_NO_WINDOW or DETACHED_PROCESS), so each arm has its own root pid and
# TokeTrace can attribute every kept token to the process that kept it, arm by arm.
# Elevated. Needs the NT Kernel Logger free (wpr -status says "WPR is not recording").
#   pwsh -NoProfile -File trace-run.ps1 -Out C:\temp\h9kdetach\t1 -Name t1 -Arms 'git:today:40','git:detached:40:hidden:detached' ...
# Arm syntax: program:mode:launches[:desktop(hidden|default)[:launcherFlags(nowindow|detached|none)[:snap]]]
param(
    [Parameter(Mandatory)] [string]$Out,
    [Parameter(Mandatory)] [string[]]$Arms,
    [string]$Name = 'trace',
    [int]$Settle = 75,
    [string]$Probe = 'C:\temp\h9kdetach\bin\probe.exe',
    [string]$TokeTrace = 'C:\temp\h9kdetach\toketrace-bin\TokeTrace.dll',
    [string]$Profile = 'C:\Users\Hallm\.hall9k\projects\hall9k\repo\wt-0b4685d2-666e4d42\docs\spikes\windows-token-leak-origin\analysis\toke-nostacks.wprp',
    [switch]$Analyze
)
$ErrorActionPreference = "Stop"
$Arms = @($Arms | ForEach-Object { $_ -split "," })
New-Item -ItemType Directory -Force $Out | Out-Null
$etl = Join-Path $Out "$Name.etl"
$h9kd = Get-Process h9kd
"h9kd before: pid $($h9kd.Id) start $($h9kd.StartTime.ToString('o'))"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2
$t0 = Get-Date
$map = @()
$i = 0
try {
    foreach ($arm in $Arms) {
        $f = $arm -split ':'
        $prog = $f[0]; $mode = $f[1]; $n = $f[2]
        $desk = if ($f.Count -gt 3) { $f[3] } else { 'hidden' }
        $lflags = if ($f.Count -gt 4) { $f[4] } else { 'nowindow' }
        $snap = if ($f.Count -gt 5) { $f[5] } else { '2' }
        $log = Join-Path $Out "$Name-arm$i-$prog-$mode.log"
        $args2 = @('standin', '--flags', $lflags, '--log', $log)
        if ($desk -eq 'hidden') { $args2 += @('--desktop', "h9kspike$PID") }
        $args2 += @('--run', $Probe, 'run', '--program', $prog, '--mode', $mode, '--n', $n, '--snap', $snap, '--windows')
        $lines = & $Probe @args2
        $pidLine = $lines | Where-Object { $_ -match '^STANDIN pid=(\d+) flags' } | Select-Object -First 1
        $armPid = [int]([regex]::Match($pidLine, 'pid=(\d+)').Groups[1].Value)
        $exitLine = ($lines | Where-Object { $_ -match 'exit=' } | Select-Object -Last 1)
        $done = (Select-String -Path $log -Pattern '^DONE|^WINDOWS' | ForEach-Object { $_.Line }) -join ' | '
        "arm $i $arm -> pid $armPid; $exitLine; $done"
        $map += [pscustomobject]@{ Index = $i; Arm = $arm; Pid = $armPid; Log = $log }
        $i++
    }
}
finally {
    "waiting $Settle s for freeable tokens to be freed"
    Start-Sleep -Seconds $Settle
    wpr -stop $etl 2>&1 | Select-Object -Last 1
}
"trace length about {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds
$map | Export-Csv (Join-Path $Out "$Name-arms.csv") -NoTypeInformation
$csv = Join-Path $Out "$Name.csv"
tracerpt $etl -of CSV -o $csv -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
if ($Analyze) {
    $roots = ($map | ForEach-Object { $_.Pid }) -join ','
    dotnet $TokeTrace $csv --roots $roots --root-image probe --by-cmd --settle 60 --spawn-rows 60 --tsv (Join-Path $Out "$Name-rows") | Out-File (Join-Path $Out "$Name-analysis.txt") -Encoding utf8
    "analysis written to $Out\$Name-analysis.txt"
}
$h9kd2 = Get-Process h9kd
"h9kd after: pid $($h9kd2.Id) start $($h9kd2.StartTime.ToString('o'))"
