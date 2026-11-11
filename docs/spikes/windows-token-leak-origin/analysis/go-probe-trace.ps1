# Is the token that gh.exe keeps without starting any child a property of gh, of Go programs, or of programs that read their own token?
# Arms (none starts a child): gh --version, docker --version (also Go), whoami (reads its own token), hostname (control).
# Elevated; needs the NT Kernel Logger free.
param([Parameter(Mandatory)] [string]$Out, [Parameter(Mandatory)] [string]$Profile, [int]$N = 25, [int]$TailSeconds = 75, [string]$Name = 'goprobe')
$ErrorActionPreference = 'Stop'
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
$arms = @(
    @('C:\Program Files\GitHub CLI\gh.exe', @('--version')),
    @((Get-Command docker.exe).Source, @('--version')),
    @('C:\Windows\System32\whoami.exe', @()),
    @('C:\Windows\System32\hostname.exe', @())
)
"helper pid $PID"
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2
function Run([string]$exe, [string[]]$a) {
    $si = [Diagnostics.ProcessStartInfo]::new($exe); $si.UseShellExecute = $false
    $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
    foreach ($x in $a) { $si.ArgumentList.Add($x) }
    $p = [Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); [void]$p.StandardError.ReadToEnd(); $p.WaitForExit(); $p.Dispose()
}
for ($i = 0; $i -lt $N; $i++) { foreach ($arm in $arms) { Run $arm[0] $arm[1] } }
Start-Sleep -Seconds $TailSeconds
wpr -stop $etl 2>&1 | Select-Object -Last 1
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
