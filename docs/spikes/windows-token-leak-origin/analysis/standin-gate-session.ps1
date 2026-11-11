# Stand-in for the two daemon launches that did not happen while the daemon was traced:
# a gate run (VerificationRunner.cs: cmd.exe /c "<gate command>", UseShellExecute=false, CreateNoWindow, MSBUILDDISABLENODEREUSE=1)
# and an agent session start (WindowsProcessManager.Spawn: cmd.exe /c ""claude" <args> < in > out 2> err").
# Same ProcessStartInfo shape, started by this script instead of h9kd. Elevated; needs the NT Kernel Logger free.
#   pwsh -NoProfile -File standin-gate-session.ps1 -Out C:\temp\tokeleak -Profile toke-nostacks.wprp -Worktree <repo> -GateCommand '<command>'
param(
    [Parameter(Mandatory)] [string]$Out, [Parameter(Mandatory)] [string]$Profile, [Parameter(Mandatory)] [string]$Worktree,
    [Parameter(Mandatory)] [string]$GateCommand, [string]$Name = 'standin', [int]$TailSeconds = 90, [int]$GateMaxMinutes = 25
)
$ErrorActionPreference = 'Stop'
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
"helper pid $PID"
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2

function Start-Wrapper([string]$command, [string]$workdir, [hashtable]$env) {
    $si = [System.Diagnostics.ProcessStartInfo]::new('cmd.exe')
    $si.Arguments = "/c `"$command`""            # WindowsCommandLine.WrapForCmdExe
    $si.WorkingDirectory = $workdir; $si.UseShellExecute = $false; $si.CreateNoWindow = $true
    foreach ($k in $env.Keys) { $si.Environment[$k] = $env[$k] }
    [System.Diagnostics.Process]::Start($si)
}

# agent session start: claude --version stands in for the session (it starts and exits without calling the API)
$in = Join-Path $Out "$Name-session-in.txt"; Set-Content $in 'x'
$session = Start-Wrapper "`"claude`" --version < `"$in`" > `"$Out\$Name-session-out.txt`" 2> `"$Out\$Name-session-err.txt`"" $Worktree @{}
$session.WaitForExit(); "agent-session stand-in cmd.exe pid $($session.Id) exit $($session.ExitCode)"

# gate run
$gate = Start-Wrapper $GateCommand $Worktree @{ MSBUILDDISABLENODEREUSE = '1' }
"gate stand-in cmd.exe pid $($gate.Id)"
if (-not $gate.WaitForExit($GateMaxMinutes * 60 * 1000)) { "gate still running after $GateMaxMinutes minutes" } else { "gate exit $($gate.ExitCode)" }
Start-Sleep -Seconds $TailSeconds
wpr -stop $etl 2>&1 | Select-Object -Last 1
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
