# Summarise bench.exe CSV output: per pair (arm A delta minus arm B delta) / launches, then mean, standard deviation and standard error.
#   pwsh -NoProfile -File summarize-batches.ps1 <batches.csv> [...]
foreach ($f in $args) {
    $rows = Import-Csv $f
    $per = foreach ($g in $rows | Group-Object pair) {
        $a = $g.Group | Where-Object arm -eq 'A'; $b = $g.Group | Where-Object arm -eq 'B'
        [pscustomobject]@{ Pair = [int]$g.Name; AFirst = ($a.order -eq 'first'); A = [int]$a.delta; B = [int]$b.delta; N = [int]$a.launches; PerLaunch = ([int]$a.delta - [int]$b.delta) / [int]$a.launches }
    }
    $v = $per.PerLaunch; $n = $v.Count; $mean = ($v | Measure-Object -Average).Average
    $sd = [math]::Sqrt((($v | ForEach-Object { ($_ - $mean) * ($_ - $mean) }) | Measure-Object -Sum).Sum / ($n - 1))
    "{0}: {1} pairs of {2} launches; per launch (A-B): mean {3:N2}, sd {4:N2}, standard error {5:N2}, min {6:N2}, max {7:N2}; pairs above 0: {8}" -f (Split-Path $f -Leaf), $n, $per[0].N, $mean, $sd, ($sd / [math]::Sqrt($n)), ($v | Measure-Object -Minimum).Minimum, ($v | Measure-Object -Maximum).Maximum, @($v | Where-Object { $_ -gt 0 }).Count
    $first = $per | Where-Object AFirst; $second = $per | Where-Object { -not $_.AFirst }
    "    A first in {0} pairs (mean {1:N2}), A second in {2} pairs (mean {3:N2}); total A {4}, total B {5}" -f @($first).Count, ($first.PerLaunch | Measure-Object -Average).Average, @($second).Count, ($second.PerLaunch | Measure-Object -Average).Average, ($per.A | Measure-Object -Sum).Sum, ($per.B | Measure-Object -Sum).Sum
}
