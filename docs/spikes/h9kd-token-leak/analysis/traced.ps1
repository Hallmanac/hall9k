param([string]$Exe, [string]$A, [int]$N=60, [string]$Tag="x")
$wprp = "C:\Users\Hallm\.hall9k\notes\toke.wprp"
$etl = "$PSScriptRoot\etl\$Tag.etl"
& wpr -cancel 2>&1 | Out-Null
& wpr -start "$wprp!TokePool" -filemode 2>&1 | Select -Last 1
Start-Sleep 1
function Run([string]$exe, [string]$a) { $si=[Diagnostics.ProcessStartInfo]::new($exe,$a); $si.UseShellExecute=$false; $si.RedirectStandardOutput=$true; $si.RedirectStandardError=$true; $p=[Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); $p.WaitForExit(); $p.Dispose() }
"pwsh pid $PID"
1..$N | % { Run $Exe $A }
Start-Sleep 1
& wpr -stop $etl 2>&1 | Select -Last 2
