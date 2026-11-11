# Runs TokeTrace on a trace csv for every arm in <Name>-arms.csv. An arm's root is told apart from a later process that reused its pid
# by the arm's own command-line fragment (--program P --mode M --n N), so pid reuse cannot move an arm's tree.
#   pwsh -NoProfile -File analyze.ps1 -Dir C:\temp\h9kdetach\tA -Name tA
param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$Name,
      [string]$TokeTrace = 'C:\temp\h9kdetach\toketrace-bin\TokeTrace.dll')
$arms = Import-Csv (Join-Path $Dir "$Name-arms.csv")
$spec = ($arms | ForEach-Object { $f = $_.Arm -split ':'; "$($_.Pid)=--program $($f[0]) --mode $($f[1]) --n $($f[2]) --snap" }) -join '|'
dotnet $TokeTrace (Join-Path $Dir "$Name.csv") --root-spec $spec --root-image probe --by-cmd --settle 60 --spawn-rows 60 --tsv (Join-Path $Dir "$Name-rows") | Out-File (Join-Path $Dir "$Name-analysis.txt") -Encoding utf8
