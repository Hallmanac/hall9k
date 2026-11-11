# What would avoiding the nested launches change? One kernel trace, five arms, 20 launches each, started from this script.
#   gh arms:  gh api user (baseline)  vs  the same with TZ set (gh then does not start tzutil)
#   git arms: git ls-remote of a private repository with the owner's credential helper (!gh auth git-credential)
#             vs the same with TZ set (the gh inside the helper does not start tzutil)
#             vs the same with the helper switched off and the token sent as a header through the environment
# The arms differ in their command line (a different ref name) so the trace can tell them apart; the token is never on a command line.
# Elevated; needs the NT Kernel Logger free.
# The run committed here used a private repository of the owner's; its name is replaced by <private-repo-N> in results/. Pass any private repository you can read as -Remote.
# Arm 5 sets an unscoped http.extraHeader, which git sends to every HTTPS host; that is fine for a ls-remote of one GitHub URL, but production use must scope it (http.https://github.com/.extraHeader, see Draft 1 in FINDINGS.md). The scoped key was not traced.
param([Parameter(Mandatory)] [string]$Out, [Parameter(Mandatory)] [string]$Profile, [Parameter(Mandatory)] [string]$Worktree, [Parameter(Mandatory)] [string]$Remote, [int]$N = 20, [int]$TailSeconds = 75, [string]$Name = 'avoid')
$ErrorActionPreference = 'Stop'
$prof = (Get-Content $Profile -Raw | Select-String 'Profile Id="([^"]+)" Name="([^"]+)"').Matches[0].Groups[2].Value
$etl = Join-Path $Out "$Name.etl"
if ((wpr -status 2>&1 | Out-String) -notmatch 'not recording') { throw 'wpr is already recording; not touching it' }
$remote = $Remote   # the HTTPS URL of a private repository you can read: git has to ask the credential helper
$git = 'C:\Program Files\Git\ucrt64\bin\git.exe'; $gh = 'C:\Program Files\GitHub CLI\gh.exe'
$token = (& $gh auth token --user Hallmanac).Trim()
$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$token"))
"helper pid $PID"
wpr -start "$Profile!$prof" -filemode 2>&1 | Select-Object -Last 1
Start-Sleep -Seconds 2
function Run([string]$exe, [string[]]$a, [hashtable]$env = @{}, [string]$dir = $Worktree) {
    $si = [Diagnostics.ProcessStartInfo]::new($exe); $si.UseShellExecute = $false; $si.CreateNoWindow = $false   # ExternalProcess.StartInfoFor does not set it either
    $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true; $si.WorkingDirectory = $dir
    foreach ($x in $a) { $si.ArgumentList.Add($x) }
    foreach ($k in $env.Keys) { $si.Environment[$k] = $env[$k] }
    $p = [Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); [void]$p.StandardError.ReadToEnd(); $p.WaitForExit(); $p.Dispose()
}
$noHelper = @{ GIT_CONFIG_COUNT = '2'; GIT_CONFIG_KEY_0 = 'credential.helper'; GIT_CONFIG_VALUE_0 = ''; GIT_CONFIG_KEY_1 = 'http.extraHeader'; GIT_CONFIG_VALUE_1 = "Authorization: Basic $basic" }
for ($i = 0; $i -lt $N; $i++) {
    Run $gh @('api', 'user', '-q', '.login')                                        # arm 1: baseline gh
    Run $gh @('api', 'user', '-q', '.id') @{ TZ = 'UTC' }                            # arm 2: gh with TZ
    Run $git @('ls-remote', $remote, 'refs/heads/main')                             # arm 3: git with the helper chain
    Run $git @('ls-remote', $remote, 'refs/heads/main', 'refs/heads/tz') @{ TZ = 'UTC' }   # arm 4: same, TZ set
    Run $git @('ls-remote', $remote, 'refs/heads/main', 'refs/heads/nohelper') $noHelper   # arm 5: no helper, header from the environment
}
Start-Sleep -Seconds $TailSeconds
wpr -stop $etl 2>&1 | Select-Object -Last 1
tracerpt $etl -of CSV -o (Join-Path $Out "$Name.csv") -summary (Join-Path $Out "$Name-summary.txt") -y 2>&1 | Select-Object -Last 2
