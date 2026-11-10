# Spike: which launch condition makes h9kd's process tree leak kernel access tokens

Spike ahead of fix task 54c44399. Findings only; nothing under `src/` changed. Everything below was measured on the Windows node itself (Windows 11 build 10.0.26300, elevated session, 28 logical processors) while h9kd kept running and serving its four projects.

## What was run

**h9kd identity, taken from `Get-Process h9kd`.** Start of spike (12:18 EST): pid 34684, started 2026-11-10 11:39:57. End of spike (14:54 EST): pid 34684, started 2026-11-10 11:39:57. They match; h9kd was never stopped, restarted or replaced. Every launch below ran in a helper process this session started itself.

**Measurement.** The live count of kernel access tokens is the pool tag `Toke` read from `NtQuerySystemInformation(SystemPoolTagInformation)` (allocations minus frees), the same counter as the node's `toke-count.ps1`. It reads about 25 a second of background growth from h9kd's own leak plus whatever else runs, so every comparison is in **alternating batches of equal wall-clock length** (launches, then idle until the window ends, so the background lands on both arms alike), with the batch's before count, after count and duration recorded. Tools are in `analysis/` and `probe/` (the probe is a small .NET exe kept as `.txt` so no build picks it up).

**Kernel trace.** The node's `toke.wprp` profile (Toke allocations with stacks, process and thread events), converted with `tracerpt` and read with the awk scripts in `analysis/`. One 30 s trace of the running daemon; two traces of launches from a helper (60 and 100 launches of each kind, one followed by 90 s of waiting to see whether the tokens are ever freed; one by 70 s); no load beyond the launches themselves.

**Hypotheses tested, in the order they were taken.**

1. Something h9kd inherits from how it is launched (log and NUL as standard handles, the Task Scheduler job, elevation, console flags) is passed down the tree.
2. How `Process.Start` itself creates children (as opposed to native `CreateProcessW`).
3. Something about the images named `git.exe` or `gh.exe`, their signature or their location.
4. Something a child does at startup (console control handler, shell32, user32, COM, Winsock).
5. Handles to tokens or process objects held in user mode.
6. What the launched program does with a child of its own (found from the 30 s trace, below, and then tested directly).

## What was observed

### The condition

**Starting a program that itself starts a child process and is still running when that child exits leaves a kernel access token allocated for good. Git for Windows' redirector `C:\Program Files\Git\cmd\git.exe`, which is what `git` resolves to for h9kd, is exactly such a program: it starts the real `ucrt64\bin\git.exe`, waits for it, and exits. The control is the real `git.exe` launched directly, which starts nothing.** The same applies to `gh.exe`, `sh.exe`, `bash.exe` and `git-remote-https.exe` when they start helpers of their own.

### Evidence 1: the running daemon's own trace (30 s, no load from this session)

h9kd made 26,999 `Toke` allocations; 479 were never freed. All 479 are on one kernel stack, the child-token allocation inside `CreateProcess`. h9kd started 491 children in the window: 432 `git.exe`, 47 `gh.exe`, 12 `ssh-keygen.exe`. Per child kind (`analysis/pairs.awk`):

| parent > child | token allocations | never freed |
|---|---:|---:|
| h9kd > git.exe (the redirector) | 432 | 432 |
| h9kd > gh.exe | 47 | 47 |
| h9kd > ssh-keygen.exe | 12 | 0 |
| git.exe > ssh-keygen.exe | 116 | 0 |
| git-remote-https.exe > sh.exe | 36 | 36 |
| sh.exe > gh.exe | 33 | 33 |
| bash.exe > bash.exe | 6 | 6 |

The programs whose token is kept are the ones that start children themselves (git redirector, gh, sh, bash, remote-https); the ones that start none (ssh-keygen, tzutil, docker, conhost) are freed. 432 + 47 = 479 is the whole unfreed total, so the leak is one token per launched program that spawns, not a stream from h9kd's own code.

### Evidence 2: lifetime of the tokens, from a helper (not h9kd)

Same helper, same `Process.Start` with redirected output, alternating launches, then 90 s of waiting, all inside one trace (`analysis/traced2.ps1`, `life.awk`):

| launched program | launches | child token freed within 90 s |
|---|---:|---:|
| `cmd\git.exe` redirector | 100 | **0** (never) |
| `hostname.exe` | 100 | 94 (average 2.35 s, longest 4.58 s) |

A second trace, 40 launches of each kind followed by 70 s: redirector 40 of 40 never freed, `cmd.exe /c hostname` 40 of 40 never freed, `hostname.exe` 38 of 40 freed; of 80 launches of a probe exe, half of them spawning a child and half not, 40 were never freed (the count agrees with the spawning half; the trace cannot see the arguments). The process objects themselves are gone (only 23 `git.exe` and 6 `hostname.exe` processes were still defunct when the 90 s trace stopped, against 100 launches each), so the tokens outlive their processes. No user-mode process gained process or token handles across a batch of redirector launches (`SystemExtendedHandleInformation` diff), so the extra kernel reference is not a handle in anyone's table.

### Evidence 3: alternating batches of live `Toke` count

All arms start the same command, `git --version`, one child at a time. Condition = the redirector, control = the real git beside it. Each batch is 200 launches. "per launch" is (condition delta minus control delta) / 200.

**Opt-in test run, six rounds (the branch's test, quoted in full below).** Order alternates every round (round 1 condition first, round 2 control first, ...).

| round | arm order | condition: duration, before > after (delta) | control: duration, before > after (delta) | per launch |
|---:|---|---|---|---:|
| 1 | cond, ctl | 8.0 s, 293340 > 293959 (+619) | 5.4 s, 293959 > 294006 (+47) | 2.86 |
| 2 | ctl, cond | 8.0 s, 294305 > 294832 (+527) | 5.2 s, 294006 > 294305 (+299) | 1.14 |
| 3 | cond, ctl | 7.6 s, 294832 > 295530 (+698) | 5.3 s, 295530 > 295701 (+171) | 2.63 |
| 4 | ctl, cond | 6.6 s, 295669 > 295995 (+326) | 4.5 s, 295701 > 295669 (-32) | 1.79 |
| 5 | cond, ctl | 7.3 s, 295995 > 296501 (+506) | 5.2 s, 296503 > 296727 (+224) | 1.41 |
| 6 | ctl, cond | 7.6 s, 297014 > 297453 (+439) | 5.3 s, 296727 > 297014 (+287) | 0.76 |

Totals over 1,200 launches per arm: condition +3,115, control +996, difference **2,119** (1.77 per launch). Every round is above 0.33 per launch (smallest 0.76). Every batch is a 15 s window.

**Earlier opt-in runs of the same test (design changed between them; kept so nothing is hidden):** 3 rounds x 200, condition always first, 20 s windows: 1.16, 1.47, 1.32 per launch (difference 790 over 600). 4 rounds x 200 with alternating order and a warm-up: 0.62, 0.60, 0.34, 0.73 per launch (difference 459 over 800); that run is the weakest and the one closest to the 0.33 floor in a single round (0.34).

**Eight pairs, helper started plainly from this session, condition always first, 20 s windows** (`results/git-8pairs.txt`, every batch's before, after and duration are in that file): per pair 2.42, 1.25, 1.88, 1.04, 1.98, 2.29, 1.44, 0.91, difference 2,643 over 1,600 launches per arm (1.65 per launch). Smallest pair 0.91.

**Same comparison from other launch contexts (3 pairs of 200 each; results in `results/`):**

| context | per pair | total difference per launch |
|---|---|---:|
| plain helper, a descendant of h9kd (`git-plain.txt`) | 2.20, 2.98, 1.14 | 2.10 |
| helper started like h9kd: `CreateProcessW`, inheritable append-only log handle as stdout and stderr, NUL stdin, `CREATE_NO_WINDOW` (`git-h9kdstyle.txt`) | 2.58, -0.29, 1.06 | 1.12 |
| unelevated helper re-parented to explorer.exe (`git-unelevated.txt`) | 2.58, -0.43, 1.04 | 1.06 |

In the last two one pair came out negative (condition arm below control by 59 and 86 tokens); that is the node's background noise, and I am reporting it rather than dropping it. The leak does not depend on elevation, on the standard handles h9kd was given, or on `CREATE_NO_WINDOW`.

### The measurement has a first-arm bias, and noise of roughly 1 token per launch per pair

A null run, real git against real git (`results/git-null.txt`, the labels "condition" and "control" there both mean real git), gave +1.25, +0.33, +0.14, +0.56 per launch (mean +0.57): the arm that goes first in a pair reads high by up to about half a token per launch. Run with the control first (`git-swapped.txt`, redirector second, 5 pairs, warm-up first) the difference was +1.31, +1.96, +0.72, +0.78, **-1.35** (mean 0.68). So the effect is real in either order but is about 1 per launch with a pair-to-pair spread near 1; **one pair out of five went the wrong way at 200 launches a batch**. This is why the test totals several rounds, alternates the order, and why the kernel-trace evidence above is the stronger proof of "one token per launch, never freed".

### Mechanism details (all exploratory; per-launch figures are live-count excess over an idle window of equal length, 100 launches a batch, 3 to 4 pairs)

The probe is one exe whose only difference between arms is whether it starts a child.

| arm | per launch |
|---|---:|
| `chain 0` (starts nothing) | -0.21 |
| `chain 1` (starts a copy of itself and waits) | 1.26, 0.91 |
| `chain 3` (three nested spawners) | 4.74 |
| `chain 5` | 4.82 |
| `detach 1` (starts a child, exits at once) | 0.05 |
| native `CreateProcessW` child, parent waits (inherit handles / not) | 0.93 / 1.24 |
| native child, parent exits at once | 0.22 |
| native child, parent sleeps 300 ms without waiting | 1.57 |
| native child, parent closes the handle then sleeps | 1.66 |
| native child created suspended and terminated | -0.32 |
| native child created suspended, resumed, waited | 0.30 (not understood) |

Cost scales with the number of spawning processes in a chain. The trigger is the parent being alive when the child exits, not waiting: sleeping or closing the handle leaks as much as waiting, exiting first does not. Alternating-batch runs of the probe (`results/probe-chain1-vs-chain0.txt`: 4.52, -0.12, 1.35; `probe-detach1-vs-chain0.txt`: 0.31, -0.28, 0.70; `cmd-vs-hostname.txt`, `cmd.exe /c hostname` against `hostname.exe`: 3.46, 0.30, 1.34) agree in direction but are noisy.

### Hypotheses that did not hold

- **Inherited handles, the log file as stdout, the NUL stdin, elevation, `CREATE_NO_WINDOW`, `DETACHED_PROCESS`, `bInheritHandles=false`:** the redirector leaks the same with each (native `CreateProcessW` against idle: plain 0.82, no inherit 1.05, no window 0.64, detached 1.15 per launch; helper contexts above). The CLI's launch code is not a cause. (I could not run the installed `h9k daemon autostart launch` itself: the platform refuses it from a dispatched session, so the h9kd-style helper above is a hand-built copy of what `WindowsDaemonLaunch.Create` does.)
- **`Process.Start` versus `CreateProcessW`:** both leak when the program spawns.
- **Image name, signature, location:** `hostname.exe` copied to `git.exe` and `gh.exe` in a temp directory does not leak (0.10, -0.11); the signed real `ucrt64\git.exe` copy does not leak (0.13).
- **A child-side API** (`SetConsoleCtrlHandler`, shell32 `SetCurrentProcessExplicitAppUserModelID`, user32, COM, Winsock, in a .NET probe): none leaks on its own (-0.22, 0.38, 0.01, -0.15, -0.08).
- **User-mode handles:** none gained (above).
- **Third-party kernel code:** not retested; the node note already shows stopping the Killer and Dell services changed nothing, and the allocating stack is the same ntoskrnl `CreateProcess` stack for freed and unfreed tokens, so no driver frame separates them. The kernel object that keeps the reference was not identified.

### The opt-in test, and one recorded run on this node

`tests/Hall9k.Tests/Connectors/WindowsTokenLeakReproTests.cs`, class `WindowsTokenLeakReproTests`, in the xUnit collection `TokenLeakRepro` (`DisableParallelization = true`). **It is skipped unless `HALL9K_TOKEN_LEAK_REPRO=1` is set** (and it only exists to run on Windows; elsewhere it is skipped too). It starts 1,200 children through the shared runner `ExternalProcess.Runner` with the redirector, and 1,200 with the real git, in alternating 200-launch batches, and **fails when the redirector arm leaves at least 100 more live `Toke` tokens than the real-git arm**.

Run: `HALL9K_TOKEN_LEAK_REPRO=1 dotnet test tests/Hall9k.Tests --no-build --filter "FullyQualifiedName~WindowsTokenLeakReproTests"` (elevated; about 3.7 minutes). Recorded run, 2026-11-10 14:50 to 14:54 EST on this node (`results/optin-run-final.txt`):

```
Failed Hall9k.Tests.Connectors.WindowsTokenLeakReproTests.Launching_a_program_that_starts_and_outlives_its_own_child_leaks_a_token_the_real_git_does_not [3 m 30 s]
Expected difference to be less than 100L because the redirector arm (C:\Program Files\Git\cmd\git.exe) left 2119 more live Toke tokens than the real git arm (C:\Program Files\Git\ucrt64\bin\git.exe) across 1200 launches each: a launched program that starts a child and is still running when it exits keeps its own kernel token alive for good, but found 2119L (difference of 2019).
TOTAL over 1200 launches per arm: condition +3115, control +996, difference 2119 (1.77 per launch)
```

Without the variable the class is reported `Skipped` and the branch's own gates are unaffected. A caution for whoever uses it as the fix's regression test: the totals carry noise of a few hundred tokens, so after a fix the difference will wander around zero by about that much and the 100 threshold is not safe from false failures; raise the launch count, or the threshold, or average several runs.

### What to try next (the kernel owner of the extra reference is still unknown)

1. A kernel debugger or object-reference tracing on one leaked token, or the same test on another Windows build: if a Windows 11 26100 box does not leak, this is a build 26300 regression to report to Microsoft, and the fix is to avoid spawner chains.
2. Understand the suspended, resumed, waited case (0.30) and the child-created-suspended path: it may show which step takes the reference.
3. For h9kd: launching the real `git.exe` instead of the redirector removes one leaked token per call, but real git, gh, sh and remote-https leak their own when they start helpers, so only fewer nested launches (or a fixed OS) removes the rest.

## Verdict

**Met, with a stated limit.** The launch condition is named and measured: a launched program that starts a child and is still running when it exits keeps a kernel access token allocated for good, and the redirector `cmd\git.exe` that h9kd reaches by `git` is exactly such a program. Three or more alternating pairs of at least 100 launches a batch show the condition arm above the control by at least 0.33 tokens a launch in every pair, in two complete series (the recorded six-round opt-in run, smallest 0.76, and an eight-pair run, smallest 0.91); other series from other contexts are in `results/` and include a pair below zero, and the null run shows a first-arm bias of up to about half a token per launch, both reported above. The pushed branch carries the opt-in Windows-only test (`HALL9K_TOKEN_LEAK_REPRO=1`), 1,200 children per arm through `ExternalProcess.Runner`, and one opt-in run on this node failed with the first set 2,119 tokens above the second. h9kd's pid and start time match at start and end (34684, 11:39:57), and nothing under `src/` changed. The limit: the cause is "a spawning program keeps its token", reproduced from every launch context tried, not a defect in how h9kd itself is started, and the kernel object that holds the reference is not identified.
