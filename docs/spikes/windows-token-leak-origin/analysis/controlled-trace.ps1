# Controlled pool trace: launches of both arms, then a wait, all inside one kernel trace.
# Elevated. Needs the NT Kernel Logger free (wpr -status says "WPR is not recording").
#   pwsh -NoProfile -File controlled-trace.ps1 -Out C:\temp\tokeleak -Profile toke.wprp [-Launches 150] [-Wait 75]
param(
    [Parameter(Mandatory)] [string]$Out,             # directory holding spawn.exe and bench.exe; the .etl/.csv go here too
    [Parameter(Mandatory)] [string]$Profile,         # path to toke.wprp (stacks) or toke-nostacks.wprp
    [string]$Name = 'controlled',
    [int]$Launches = 150,
    [int]$Wait = 75,
    [string]$ArmA = '1', [string]$ArmB = '0'
)
$ErrorActionPreference = 'Stop'
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2
$t0 = Get-Date
# one pair: arm A's batch, then arm B's batch, each in a 20 s window (bench's own idle fills it)
$p = Start-Process -FilePath (Join-Path $Out 'bench.exe') -ArgumentList "`"$(Join-Path $Out 'spawn.exe')`" $ArmA $ArmB $Launches 20 1" `
    -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$Name-bench.csv") -RedirectStandardError (Join-Path $Out "$Name-bench.err.txt")
$p.WaitForExit()
"bench pid $($p.Id), exit $($p.ExitCode), launches done at $((Get-Date).ToString('o'))"
Start-Sleep -Seconds $Wait                           # long enough for every freeable token to be freed
wpr -stop $etl 2>&1 | Select-Object -Last 1
"trace length about {0:N0} s" -f ((Get-Date) - $t0).TotalSeconds
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 3
