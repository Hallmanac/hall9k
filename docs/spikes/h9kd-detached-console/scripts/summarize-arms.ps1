# Reads <Name>-analysis.txt (TokeTrace --root-spec output) and <Name>-arms.csv and prints, per arm, the kept (never freed) child tokens
# by "creator > child" image, the totals, and kept per launch.
#   pwsh -NoProfile -File summarize-arms.ps1 -Dir C:\temp\h9kdetach\tA -Name tA [-Detail]
param([Parameter(Mandatory)][string]$Dir, [Parameter(Mandatory)][string]$Name, [switch]$Detail)
$arms = Import-Csv (Join-Path $Dir "$Name-arms.csv")
$lines = Get-Content (Join-Path $Dir "$Name-analysis.txt")
function Short([string]$s) {
    $s = $s -replace 'C:\\Program Files\\Git\\ucrt64\\libexec\\git-core\\', 'git-core\' -replace 'C:\\Program Files\\Git\\ucrt64\\bin\\', 'ucrt64\' -replace 'C:\\Program Files\\Git\\usr\\bin\\', 'usr\' `
        -replace 'C:\\Program Files\\Git\\bin\\', 'bin\' -replace 'C:\\Program Files\\GitHub CLI\\', '' -replace 'C:\\Windows\\System32\\', '' -replace 'C:\\Temp\\h9kdetach\\bin\\', '' -replace 'C:\\Users\\Hallm\\.local\\bin\\', ''
    $s
}
$byRoot = [ordered]@{}
$cur = $null
$section = ''
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    if ($line -match '^=+ ROOT (\d+) =+') { $cur = $Matches[1]; $byRoot[$cur] = [ordered]@{ Rows = @(); Rule = @(); Found = $true }; $section = 'rows'; continue }
    if ($null -eq $cur) { continue }
    if ($line -like 'rule check*') { $section = 'rule'; continue }
    if ($line -like 'unfreed Toke*' -or $line -like 'what the creators*') { $section = 'other'; continue }
    if ($section -eq 'rule' -and $line -match '^\s+(.+?)\s+freed\s+(\d+)\s+never freed\s+(\d+)') { $byRoot[$cur].Rule += [pscustomobject]@{ Category = $Matches[1]; Freed = [int]$Matches[2]; Never = [int]$Matches[3] }; continue }
    if ($section -eq 'rows' -and $i + 1 -lt $lines.Count -and $line.Contains(' > ') -and $lines[$i + 1] -match '^\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*$') {
        $n = @($Matches[1], $Matches[2], $Matches[3], $Matches[4])
        $idx = $line.IndexOf('  <=  ')
        $head = if ($idx -ge 0) { $line.Substring(0, $idx) } else { $line }
        $cmd = if ($idx -ge 0) { $line.Substring($idx + 6) } else { '' }
        # head is "creator > child" with full paths; split on the first " > " that follows an .exe
        $split = [regex]::Match($head, '^(?<c>.+?\.exe) > (?<k>.+)$')
        $byRoot[$cur].Rows += [pscustomobject]@{ Creator = Short $split.Groups['c'].Value; Child = Short $split.Groups['k'].Value; Cmd = $cmd; Created = [int]$n[0]; Freed = [int]$n[1]; Never = [int]$n[2]; Pending = [int]$n[3] }
    }
}
foreach ($a in $arms) {
    $r = $byRoot[[string]$a.Pid]
    $launches = [int](($a.Arm -split ':')[2])
    $never = [int](($r.Rows | Measure-Object Never -Sum).Sum)
    $created = [int](($r.Rows | Measure-Object Created -Sum).Sum)
    $pending = [int](($r.Rows | Measure-Object Pending -Sum).Sum)
    "### arm $($a.Index) [$($a.Arm)] root pid $($a.Pid): child creations $created, tokens kept (never freed) $never, pending $pending; $launches launches -> kept per launch {0:N2}" -f ($never / $launches)
    $r.Rows | Group-Object { "$($_.Creator) > $($_.Child)" } | ForEach-Object {
        $cr = ($_.Group | Measure-Object Created -Sum).Sum; $nv = ($_.Group | Measure-Object Never -Sum).Sum; $fr = ($_.Group | Measure-Object Freed -Sum).Sum
        if ($Detail -or $nv -gt 0) { "    {0,-62} created {1,4} freed {2,4} kept {3,4}" -f $_.Name, $cr, $fr, $nv }
    }
    if ($Detail) { $r.Rule | ForEach-Object { "      rule: {0,-72} freed {1,4} never {2,4}" -f $_.Category, $_.Freed, $_.Never } }
}
