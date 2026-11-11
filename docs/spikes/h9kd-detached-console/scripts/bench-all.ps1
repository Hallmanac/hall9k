# Runs the alternating-batch benches for the four programs, today's start and the detached start each against a null that starts no child.
# Output: one log per bench under C:\temp\h9kdetach\b2 and a combined transcript.
$ErrorActionPreference = 'Continue'
$s = 'C:\temp\h9kdetach\scripts\bench-run.ps1'
$out = 'C:\temp\h9kdetach\b2'
New-Item -ItemType Directory -Force $out | Out-Null
$runs = @(
    @{ Name = 'git-today-vs-null';    A = 'git:today';       B = 'git-null:today';     L = 100; W = 115; P = 4; F = 'A' },
    @{ Name = 'git-detached-vs-null'; A = 'git:detached';    B = 'git-null:today';     L = 100; W = 115; P = 4; F = 'B' },
    @{ Name = 'bash-today-vs-null';   A = 'bash:today';      B = 'bashusr-null:today'; L = 200; W = 65;  P = 4; F = 'A' },
    @{ Name = 'bash-detached-vs-null';A = 'bash:detached';   B = 'bashusr-null:today'; L = 200; W = 65;  P = 4; F = 'B' },
    @{ Name = 'sh-today-vs-null';     A = 'sh:today';        B = 'sh-null:today';      L = 200; W = 80;  P = 4; F = 'A' },
    @{ Name = 'sh-detached-vs-null';  A = 'sh:detached';     B = 'sh-null:today';      L = 200; W = 80;  P = 4; F = 'B' },
    @{ Name = 'gh-today-vs-null';     A = 'gh:today';        B = 'hostname:today';     L = 200; W = 130; P = 3; F = 'A' },
    @{ Name = 'gh-detached-vs-null';  A = 'gh:detached';     B = 'hostname:today';     L = 200; W = 130; P = 3; F = 'B' }
)
foreach ($r in $runs) {
    "=== $($r.Name) ($(Get-Date -Format HH:mm:ss))"
    pwsh -NoProfile -File $s -Out $out -Name $r.Name -A $r.A -B $r.B -Launches $r.L -Window $r.W -Pairs $r.P -First $r.F
}
"=== done $(Get-Date -Format HH:mm:ss)"
