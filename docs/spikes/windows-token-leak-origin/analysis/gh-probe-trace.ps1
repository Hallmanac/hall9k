# Why do some gh runs with TZ set (so no tzutil child) still keep their token? One trace, several gh and curl arms, no child expected in any of them.
# Elevated; needs the NT Kernel Logger free.
param([Parameter(Mandatory)] [string]$Out, [Parameter(Mandatory)] [string]$Profile, [int]$N = 20, [int]$TailSeconds = 75, [string]$Name = 'ghprobe')
$ErrorActionPreference = 'Stop'
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
$gh = 'C:\Program Files\GitHub CLI\gh.exe'
"helper pid $PID"
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2
function Run([string]$exe, [string[]]$a, [hashtable]$env = @{}) {
    $si = [Diagnostics.ProcessStartInfo]::new($exe); $si.UseShellExecute = $false
    $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    foreach ($x in $a) { $si.ArgumentList.Add($x) }
    foreach ($k in $env.Keys) { $si.Environment[$k] = $env[$k] }
    $p = [Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); [void]$p.StandardError.ReadToEnd(); $p.WaitForExit(); $p.Dispose()
}
$tz = @{ TZ = 'UTC' }
for ($i = 0; $i -lt $N; $i++) {
    Run $gh @('--version') $tz                                           # no network, no auth
    Run $gh @('auth', 'token', '--user', 'Hallmanac') $tz                # local credential read only
    Run $gh @('api', 'user', '-q', '.id') $tz                            # one HTTPS call
    Run $gh @('api', 'rate_limit', '-q', '.rate.limit') $tz              # another HTTPS call
    Run 'C:\Windows\System32\curl.exe' @('-s', '-o', 'NUL', 'https://api.github.com/zen')   # HTTPS from a program that is not gh
}
Start-Sleep -Seconds $TailSeconds
wpr -stop $etl 2>&1 | Select-Object -Last 1
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
