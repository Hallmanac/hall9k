param([string]$Exe="C:\Windows\system32\hostname.exe", [string]$A="", [int]$N=500, [int]$Pairs=5)
. "$PSScriptRoot/toke.ps1"
function Run([string]$exe, [string]$a) { $si=[Diagnostics.ProcessStartInfo]::new($exe,$a); $si.UseShellExecute=$false; $si.RedirectStandardOutput=$true; $si.RedirectStandardError=$true; $p=[Diagnostics.Process]::Start($si); [void]$p.StandardOutput.ReadToEnd(); $p.WaitForExit(); $p.Dispose() }
$res = 1..$Pairs | % {
  $c = Measure-Batch "cond" $N { Run $Exe $A }
  $k = Measure-Batch "idle" 1 { Start-Sleep -Milliseconds ([int]($c.Sec*1000)) }
  [pscustomobject]@{ Pair=$_; LaunchDelta=$c.Delta; IdleDelta=$k.Delta; Sec=$c.Sec; ExcessPerLaunch=[math]::Round(($c.Delta-$k.Delta)/$N,2) }
}
$res | ft -auto | Out-String
"mean excess/launch: " + [math]::Round(($res | measure ExcessPerLaunch -Average).Average,2)
