param([int]$N=40, [int]$Wait=70, [string]$Tag="mix")
$wprp = "C:\Users\Hallm\.hall9k\notes\toke.wprp"; $etl = "$PSScriptRoot\etl\$Tag.etl"
$P = 'C:\Users\Hallm\.hall9k\scratch\probe\bin\Release\net10.0\probe.exe'
& wpr -cancel 2>&1 | Out-Null
& wpr -start "$wprp!TokePool" -filemode 2>&1 | Select -Last 1
Start-Sleep 1
function Run([string]$exe, [string]$a) { $si=[Diagnostics.ProcessStartInfo]::new($exe,$a); $si.UseShellExecute=$false; $si.RedirectStandardOutput=$true; $si.RedirectStandardError=$true; $p=[Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); $p.WaitForExit(); $p.Dispose() }
"pwsh pid $PID"
1..$N | % { Run 'C:\Windows\System32\cmd.exe' '/c hostname'; Run $P 'spawn'; Run 'C:\Windows\System32\hostname.exe' ''; Run 'C:\Program Files\Git\cmd\git.exe' '--version'; Run $P 'none' }
Start-Sleep $Wait
& wpr -stop $etl 2>&1 | Select -Last 1
