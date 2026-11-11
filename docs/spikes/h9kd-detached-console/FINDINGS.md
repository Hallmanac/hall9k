# Can hall9k stop the Windows console-launch token leak by starting its children without a console?

Spike (prototype); findings only. Nothing under `src/` changed. Times are EST, 2026-11-11, measured on the Windows node itself (Windows 11 Pro, build 26300), elevated session. Follows spike b0844e14, fix 54c44399 (#862) and research 6cf289d0 (#863).

## Answer in brief

1. **No.** Starting a child with `DETACHED_PROCESS` (stdio redirected) keeps the same number of tokens per launch as today's start, for all four programs, by the kernel pool trace: git 7.00 against 7.00, gh 1.00 against 1.00, sh 3.00 against 3.00, `bin\bash.exe` 4.00 against 4.00 (30 to 40 launches each); the agent CLI 21 against 22 a session (4 sessions each; only the CLI's own token differs). The same invocations that start no child keep 0 in both modes.
2. **The leak does not need a console on any process in the chain.** A spawner and its child both started detached (nothing in the chain attached to a console) still keeps one token a launch (40 of 40). Git's own chain under a detached start (`git.exe`, its dispatcher, `git-remote-https.exe`) has no console at all and keeps 3 of 3 a launch.
3. **What stops it is a console window that is visible**, not the absence of a console. In every arm where the creator, or the process it created, sat on a console with a visible window, no token was kept (0 of several hundred launches of the real programs, and 0 of 28 matrix cells). A console whose window is hidden (`SW_HIDE`) or absent (`CREATE_NO_WINDOW`) keeps tokens like any other. Each such arm shows a console window per child, so none is usable on the owner's desktop.
4. **Detaching also breaks things**: a detached `cmd.exe` drops the output of anything it redirects (the agent-session wrapper writes an empty file and exits 1); detached `gh` gives each `tzutil` child a new console and a window; MSYS2 `sh` and `bash` re-attach to a parent's console or build their own.
5. **h9kd already has a console** of its own (windowless, `CREATE_NO_WINDOW`), whether the logon task or `h9k daemon start` launched it. Starting it detached stops the leak in the stand-in, but only because each console child it then starts gets a new visible console window (one per launch); with `CreateNoWindow` added to those starts (a `src/` change) the leak is back at 7, 1, 3 and 4 a launch.

## What was run

**Identity of the node's h9kd (exit criterion).** Recorded 09:05:25: pid 34684, started 2026-11-10 11:39:57. **That process did not survive the run, and not because of this session:** `h9kd.log` has `Graceful stop requested via ...\h9kd.stop` at 09:10:02 and `Application started` at 09:10:35; the new pid 47288 (started 09:10:30, parent `h9k.exe daemon autostart launch`, under `cmd.exe` under `wscript.exe`) is the same at the start and the end of every trace after it (09:17 to 12:00). The node journal (`~/.hall9k/journal.md`, re-orientation log) records "hall9k-2c updated the node to 0.18.1 on Brian's direct word, mid-spike". This session never wrote the stop file, never called `h9k daemon`, and never stopped a process: every launch below ran in a helper this session started itself. The only calls made on h9kd were read-only queries (process list, console host, log tail).

**Tools (all under this directory).**
- `probe/` (kept as `.txt`; build with `dotnet build -c Release`): one .NET exe. `run` starts one of the programs below N times, one after another, in a named start mode and reports exit codes and output sizes; `--snap` samples the process tree under each launch every few milliseconds and records each process's console host (`NtQueryInformationProcess` class 49) and command line; `--windows` records new visible top-level windows; `bench` runs the alternating-batch live `Toke` count (the node's `toke-count.ps1` method, batches of equal wall-clock length, order swapped every pair); `standin` starts a process the way `WindowsDaemonLaunch.Create` starts h9kd (`CreateProcessW`, NUL stdin, an append handle on a log as stdout and stderr, `bInheritHandles`, `CREATE_NO_WINDOW` or `DETACHED_PROCESS`).
- `analysis/TokeTrace/` is the research task's tool with one addition: it takes several roots, each told apart from a later process that reused its pid by the root's command-line fragment (`--root-spec`). `scripts/trace-run.ps1` starts the research's `toke-nostacks.wprp` kernel trace (process, image-load and `Toke` pool events), runs a list of arms, each a `probe run` started by `standin`, waits 70 to 80 s so every freeable token is freed, stops the trace and converts it with `tracerpt`; `scripts/analyze.ps1` and `scripts/summarize-arms.ps1` attribute every token kept for good (never freed, process gone at least 60 s before the trace stopped) to the process that kept it.

**The measure of record is the kernel trace; the live-count batches are the cross-check.** Every arm in a trace is its own stand-in-started launcher, so its tree is attributed on its own. "Kept" below means the child's token was never freed.

**Start modes.** *Today*: the shape the code uses. For `git`, `gh`, `sh`, `bash`: `ExternalProcess.StartInfoFor`'s shape (`Process.Start`, `UseShellExecute=false`, stdout and stderr redirected, no `CreateNoWindow`, so the child inherits the launcher's console), `NonInteractiveGit` environment and the real git first on PATH (`GitForWindowsLauncher`). For the agent CLI: `WindowsProcessManager.Spawn`'s shape, `cmd.exe /c ""claude" -p --output-format stream-json --verbose --model claude-haiku-5-5 --max-turns 4 --dangerously-skip-permissions --strict-mcp-config < in > out 2> err"` with `CreateNoWindow=true`, and `CLAUDE_CODE_GIT_BASH_PATH` set as `ApplyToCurrentProcess` sets it. *Detached*: `CreateProcessW` with `DETACHED_PROCESS`, stdout and stderr on anonymous pipes the launcher reads, stdin NUL, same command line and environment. The launcher itself was a stand-in started with `CREATE_NO_WINDOW` (its own windowless console, like h9kd) unless an arm says otherwise.

**The four programs, each with a null that starts no child of its own.**

| program | invocation (starts a child of its own) | null (same shape, no child) |
|---|---|---|
| git over https | `ucrt64\bin\git.exe ls-remote https://github.com/Hallmanac/hall9k-smoke.git HEAD` (a private repo: the owner's `credential.helper` runs `sh -c gh auth git-credential get` and `store`, `gh` runs `tzutil`) | `git --version` |
| gh calling `tzutil` | `gh api user -q .login` (starts `tzutil /g` twice with no `TZ` set) | `hostname.exe` (`gh --version` also starts `tzutil`, so it is not a null: 1.00 in both modes) |
| MSYS2 sh and bash chain | `sh.exe -c "/c/Windows/System32/hostname.exe && /usr/bin/uname -s && /c/Windows/System32/hostname.exe"`; the same with `Git\bin\bash.exe` (Claude Code's launcher) and with `usr\bin\bash.exe` | `sh -c "echo hi"`; `usr\bash.exe -c "echo hi"` (`bin\bash.exe -c "echo hi"` keeps 1: the launcher starts `usr\bash.exe`) |
| agent CLI | the `cmd.exe` session wrapper above, running one prompt that makes the model call the Bash tool (about six Bash calls a session); also `claude.exe` started directly | `claude --version` through the same wrapper; directly |

**Traces** (`results/`): `t0` calibration (12 launches); `tA` core, 29 arms, git 30 and the rest 40 launches an arm; `tB` and `tC` 72 arms of a spawner-and-child matrix (20 launches each); `tD` agent CLI through `cmd.exe`, 4 sessions an arm; `tE` agent CLI directly and `hostname`; `tF` the Git launcher and visible-console roots; `tG` the stand-in with no console starting children with `CreateNoWindow`. **Benches** (`results/benches/`): alternating batches for each program, today's start and detached start each against its null.

**Private desktop.** Detached starts give some children a new console, and a console window is created on the desktop it is started on. All traced arms ran under a desktop created for the run (`CreateDesktop`, via `STARTUPINFO.lpDesktop`, gone when its last process exited), so no window was ever drawn on the owner's screen. Validity check: today's start of the git chain leaks 7 per launch on the default desktop (12 launches, 84 kept) exactly as on the private one (84 kept, `t0`). No system or user setting was changed; `ForegroundLockTimeout` was never touched.

## What was observed

### 1. Kept tokens per launch: today against detached against null (kernel trace)

Launcher with its own windowless console (like h9kd). "kept/launch" = tokens never freed divided by launches. Per-process rows are in `results/tA/tA-summary.txt` and the sibling summaries.

| program | today | detached | null (today / detached) | what keeps its token, same in both modes |
|---|---:|---:|---|---|
| git ls-remote (30 launches) | 7.00 | 7.00 | 0 / 0 | real `git.exe` 1, its `git remote-https` dispatcher 1, `git-remote-https.exe` 1, two `sh.exe` 2, two `gh.exe` 2; `tzutil` 0 |
| gh api user (40) | 1.00 | 1.00 | `hostname` 0 / 0 (`gh --version` 1.00 / 1.00) | `gh.exe` 1; its two `tzutil` 0 |
| sh chain (40) | 3.00 | 3.00 | 0 / 0 | `sh.exe` 1, its two forked `sh.exe` 2; `hostname`, `uname` 0 |
| `bin\bash.exe` chain (40) | 4.00 | 4.00 | `bin\bash -c echo` 1 (launcher) / 1 | `bin\bash.exe` 1, `usr\bash.exe` 1, two forked 2 |
| `usr\bash.exe` chain (40) | 3.00 | 3.00 | `usr\bash -c echo` 0 / 0 | the same minus the launcher |
| agent CLI through `cmd.exe` (4 sessions) | 23.00 | **broken**: exit 1, empty output, 0 kept because the CLI never ran | `claude --version` via `cmd.exe`: 1.00 / 0.00, output file empty when detached | `cmd.exe` 1, `claude.exe` 1, `claude > bin\bash` 6, `bin\bash > usr\bash` 6, `usr\bash` forks 9 |
| agent CLI started directly (4 sessions) | 22.00 | 21.00 | 0 / 0 | as above but `claude.exe` itself is **not** kept when started detached (0 of 4); 6 + 6 + 9 below it are kept the same |
| Git for Windows launcher `cmd\git.exe --version` (40) | 1.00 | 1.00 | | the launcher; the real git it starts is freed |

**For each program, does the leak stop?** Not for the program itself and not for anything below it, with one exception: a directly started agent CLI's own token (1 of 22). git: no, at every one of its six process kinds. gh: no. sh and bash: no, at every level. Agent CLI: no, except its own token; through the `cmd.exe` wrapper the detached start does not run at all.

### 2. Which console each process had (evidence)

The per-process console host was sampled from outside while each chain ran (`results/console-evidence-a.md`, and the `CHAIN` lines in each arm's log). A kernel-trace cross-check: every new console shows as a `conhost.exe` whose parent is the process that owns it (e.g. `git.exe > conhost.exe 30 created`).

- **git, today:** `git.exe`, dispatcher, `git-remote-https`, both `sh`, both `gh`, `tzutil`: all **inherited** the launcher's console.
- **git, detached:** real `git.exe`, dispatcher and `git-remote-https.exe`: **none** (`console=NONE`). Both `sh.exe`: a **new** console (MSYS2 starts `cygwin-console-helper.exe`, which owns a new `conhost.exe`). Both `gh.exe`: inherited that new console. So the three git processes keep their tokens with no console at all.
- **gh, today:** `gh.exe` and both `tzutil`: inherited. **detached:** `gh.exe` none; each `tzutil` a new console with its own `conhost.exe` (80 consoles for 40 launches).
- **sh, detached:** the first `sh.exe` showed the launcher's own console. Checked directly (`results/sh-detached-console-check.txt`): an `sh.exe -c "sleep 5"` started with `DETACHED_PROCESS` by a parent that has a console reports that parent's console host (MSYS2 re-attaches to the parent's console). Its forked `sh.exe` children, `hostname.exe` and `uname.exe`: a new console each when the parent had none.
- **bash:** same as sh.
- **agent CLI, detached, directly:** `claude.exe` none; every child it starts (`git`, `reg`, `rg`, `bash`, hook scripts) gets a **new** console and its own `conhost.exe` (`claude.exe > conhost.exe` 12, `git.exe > conhost.exe` 36 over four sessions). Through `cmd.exe`: `cmd.exe` none, `claude.exe` a new console with a visible window.
- **Git launcher, detached:** `cmd\git.exe` none; the real `git.exe` it starts gets a **new** console (`conhost.exe` per launch). Today both inherit. This is the answer to the first spike's puzzle (next section).

Caveat: the sampler reads each process every few milliseconds and keeps the last value it could read; a process that exits before its console is set up shows as `unknown`, and very short-lived children can be missed. The trace's `conhost.exe` parentage was used where they disagreed.

### 3. Reconciling with the first spike (Git for Windows' launcher with `DETACHED_PROCESS` still left about one token a launch)

Reproduced: `cmd\git.exe --version` leaves 1.00 per launch started plainly and 1.00 started detached (40 launches each, trace `tF`). The first spike detached only the launcher. The launcher has no console then, and **the real `git.exe` that it starts is given a new console** (a new `conhost.exe` per launch, `CHAIN gitcmd/detached`), so the launcher's own child creation was not console-free. More to the point, the matrix below shows that even a fully console-free chain keeps the token, so the launcher's token would have been kept either way. The first spike's 1.15 against 0.82 differences are the same effect inside its noise.

### 4. What does and does not stop the leak (spawner-and-child matrix, `tB`, `tC`)

A root (the stand-in) starts a spawner; the spawner starts a child that exits at once and waits for it. Kept = the spawner's own token (20 launches per cell; every cell is 1.00 or 0.00, never between). Start modes: `inherit` and `nowindow` (`Process.Start`, without and with `CreateNoWindow`), `detached`, `newconsole` (`CREATE_NEW_CONSOLE`, a visible window on the private desktop), `hidden` (`CREATE_NEW_CONSOLE` with `SW_HIDE`).

| root (stand-in) console | spawner started by root as | kept per launch, whatever the spawner then does to start its child (`inherit`, `nowindow`, `detached`, `newconsole`) |
|---|---|---|
| windowless (`CREATE_NO_WINDOW`, h9kd's) | inherit, nowindow, detached | 1.00 in all 12 cells |
| windowless | newconsole (visible window) | 0.00 in all 4 cells |
| none (`DETACHED_PROCESS`) | inherit (so it gets a new, visible console) | 0.00 in all 4 cells |
| none | nowindow or detached | 1.00 in all 8 cells |
| none | newconsole | 0.00 in all 4 cells |
| visible window (`CREATE_NEW_CONSOLE`) | any of the four | 0.00 in all 16 cells |
| hidden window (`SW_HIDE`) | any of the four | 1.00 in all 16 cells |
| windowless, or none | hidden (`SW_HIDE` window) | 1.00 in all 8 cells |

The reading that fits every cell: **a token is kept when neither the creator nor the created process is on a console with a visible window**; a hidden or absent window counts as none. With the real programs the same holds: a stand-in with its own visible console window keeps 0 for git (30 launches), gh (40), sh (40), bash (40) and the agent CLI started directly (3 sessions, 217 children, `tF`); a stand-in with no console that starts the programs the way today's code does (so each gets a new visible console) keeps 0 for git, gh, sh and bash (`tA`). That is consistent with the outside report that the kept reference is taken on the foreground-launch path and that `ForegroundLockTimeout=0` stops it. This run does not identify the kernel object itself, and does not explain why one directly started `claude.exe`, which starts children that get windowless consoles, is not kept when detached while a plain `probe` spawner in the same position is.

### 5. Redirected stdio and normal behaviour under the detached start

- **git** (`results/stdio-check.log`): exit codes and output are the same in both modes: `ls-remote` of a private repo returns the same sha, exit 0; `ls-remote` of a repo that does not exist returns exit 128 and `remote: Repository not found.` in both. Credential prompts stay suppressed (`GIT_TERMINAL_PROMPT=0`; the `gh` helper answers without a console). No visible window was seen.
- **gh**: same output (`Hallmanac`), exit 0. **Breaks:** each `tzutil` child gets a new console; a visible `ConsoleWindowClass` window titled `tzutil.exe` appeared on the private desktop 23 times over 80 launches (the 10 ms poll misses short ones), and 8 times for `gh --version`. On a real desktop that is a flashing window per `gh` call, about 1,365 calls an hour on the node by the research's count.
- **sh and bash**: exit codes and output the same; no visible window seen, but each starts `cygwin-console-helper.exe` and a `conhost.exe`: about 4 extra processes per launch.
- **agent CLI through `cmd.exe`: breaks.** Started detached, `cmd.exe` runs `hostname > file` and the file is empty (0 bytes against 15 bytes when started with `CREATE_NO_WINDOW`), a window titled `hostname` appears, and `claude -p ... < in > out 2> err` writes nothing and exits 1 (4 of 4). `claude --version` through it also writes an empty file. The wrapper's redirection depends on `cmd.exe` having a console.
- **agent CLI started directly, detached:** works: same 11.9 KB of stream-json, exit 0, the Bash tool ran (4 of 4), no visible window seen. Its stdin is NUL, so a prompt has to come on the command line or a pipe instead of `< in`.
- **Git launcher detached:** works, `--version` output and exit 0, no window.

### 6. Does h9kd have a console, and would starting it without one be enough?

- **It has one, windowless, from both launchers.** The running h9kd (pid 47288) owns a `conhost.exe` child created at its own start (11272), and its console host is that process: `ProcessConsoleHostProcess` returns it and `WindowsDaemonLaunch.cs:61`/`:264` pass `CREATE_NO_WINDOW`. The logon task runs `wscript.exe //B h9kd-autostart-launch.vbs`, which `Run`s `cmd.exe /c ... h9k.exe daemon autostart launch` hidden (window style 0); `h9k.exe` inherits that hidden console and starts h9kd through `WindowsDaemonLaunch.Create` (`CREATE_NO_WINDOW`), which gives h9kd a console of its own. The live process tree 09:10:29 to 09:10:30 shows exactly that chain (wscript 41888, cmd 46864, h9k 40024, h9kd 47288). `h9k daemon start` reaches the same `Create` (`StartDetached`), so h9kd has the same kind of console either way. It was not relaunched to check; the stand-in below was started with the same call shape (`standin --flags nowindow`: NUL stdin, log handle as stdout and stderr, `CREATE_NO_WINDOW`) and reports `consoleHost` set, `consoleWindow=0`, and its children inherit it.
- **Detached h9kd, children as today: no leak, one visible window per child.** Stand-in with `DETACHED_PROCESS` and children started as `ExternalProcess` starts them: 0 kept for git (30), gh (40), sh (40), bash (40), and a visible console window on the private desktop for every launch (30 windows for 30 git launches, 40 for 40 gh, 40 for 40 sh, 40 for 40 bash). On the owner's desktop that is a window for every git and gh call.
- **Detached h9kd and `CreateNoWindow` on every child start: the leak is back** (`tG`): git 7.00, gh 1.00, sh 3.00, bash 4.00 a launch, no windows. For the agent session wrapper, which already sets `CreateNoWindow`, a detached h9kd changes nothing (23 kept a session).
- **So starting h9kd without a console is not enough on its own:** it stops the leak only by turning every child into a visible console window.

### 7. Live-count cross-check (alternating batches against a null, 4 pairs; swapped order each pair)

`probe bench` inside a stand-in on the private desktop; the node's background growth is about 6 to 10 `Toke` a second (h9kd's own leak and other sessions), so a batch window of 65 to 130 s carries 600 to 1,300 background tokens; every pair's per-launch figure is (arm minus null) divided by launches. Full CSV in `results/benches/`.

| program | launches / batch, pairs | today minus null, per launch (per pair) | detached minus null | trace says |
|---|---|---|---|---:|
| git | 100, 4 | 5.24 (6.16, 2.93, 5.93, 5.94) | 5.33 (3.94, 7.76, 3.71, 5.90) | 7.00 / 7.00 |
| bash (`bin\bash`) | 200, 3 and 4 | 3.48 (3.90, 2.29, 4.26; the bench stopped itself in the fourth pair: launches took 71 s of a 65 s window) | 3.09 (3.38, 3.87, 1.46, 3.63) | 4.00 / 4.00 minus 0 |
| sh | 200, 4 | 3.25 (2.76, 3.58, 3.24, 3.41) | 1.58 (1.53, 1.89, 1.39, 1.52) | 3.00 / 3.00 |
| gh | 200, 3 | 0.19 (-0.04, -0.18, 0.79) | -0.76 (-1.76, -0.77, 0.24) | 1.00 / 1.00 |

The live count agrees with the trace in direction and roughly in size for git, bash and sh-today (about 75 to 100% of the traced tokens; pair spread about 1 to 3 per launch), the arm that goes first reads high, and the gh batches cannot see 1 a launch against this background (the null's own batches differ by that much). **Two things the live count does not support and this run did not resolve:** detached sh reads 1.58 against 3.00 in the trace, with a tight spread across pairs; and detached batches run longer than today's (a launch phase of 70 s against 40 s), so the tokens freed late weigh differently. The trace, which attributes each token to a process, is what the answers above rest on. The batches show no arm where detached is clearly below today's for git and bash, and none where it is near the null.

### 8. The opt-in Windows test on the branch

`tests/Hall9k.Tests/Connectors/WindowsDetachedStartTokenLeakTests.cs` (skipped unless `HALL9K_DETACHED_START_REPRO=1`, on Windows, off GitHub Actions, elevated, with Git for Windows' layout; its own collection, not `RealProcessSpawn`, so no gate runs it). It starts Git for Windows' launcher plainly through `ExternalProcess.Runner`, started detached (`CreateProcessW`, `DETACHED_PROCESS`, redirected pipes), and the real git as the null, in rotating alternating batches of 200 (1,200 each). Recorded run (`results/optin-run.txt`, 5 m 47 s, passed): plain +1834, detached +2077, null +1008; **plain minus null 826 (0.69 a launch), detached minus null 1,069 (0.89 a launch).** The test asserts that both stay at least 400 above the null, i.e. it records that a detached start does not avoid the leak and fails the day that stops being true. **It is not the test the exit criterion's first branch describes** (a detached start leaving no extra tokens), because no such result exists.

### 9. Limits

- The trace counts are exact per process; per-launch rates are over 20 to 40 launches (agent CLI: 4 sessions of 23 tokens), and every arm is all-or-nothing, so more launches would not change a rate. The trace says nothing about launches by an h9kd that has run for days.
- All traced arms ran on a private desktop. One cross-check (git today, 12 launches, 84 kept) matches the default desktop; no detached or visible-console arm was run on the default desktop, to avoid putting windows on the owner's screen.
- The kept-token rule "neither side on a visible console window" fits all 72 matrix cells and the real programs, but the kernel object is not identified, and one directly started `claude.exe` is the exception that is not explained.
- The model used for the agent sessions was `claude-haiku-5-5`, four sessions of about six Bash calls each per arm, not h9kd's real prompts.
- A gate run (`cmd.exe /c <gate>`) was not re-measured: it is the same `cmd.exe` shape as the session wrapper, which detached breaks.

## Verdict

**Exit criterion: met on its second branch, with a qualification the reviewer should weigh.** The document states, from kernel pool traces attributing every kept token to a process (with a no-child null for each program and an alternating-batch live count against a null as cross-check), that starting children with no console does **not** stop the leak, for git, gh, the MSYS2 sh and bash chain, or the agent CLI, for the program itself or anything below it (git 7 against 7, gh 1 against 1, sh 3 against 3, bash 4 against 4, agent CLI 21 against 22 a session). It records the console each process had, reconciles the first spike (the Git launcher's child gets a new console, and a console-free chain keeps the token anyway), records that detaching breaks `cmd.exe` redirection and makes `gh` and every `claude` child open console windows, and states that h9kd already has a windowless console from both launchers and that starting it without one stops the leak only by making each child a visible console window.
The qualification: the second branch says "why no hall9k-side start mode avoids the leak", and that is not quite what was found. The leak is avoided whenever the creator or the created process is on a console with a *visible* window; no start mode that keeps windows invisible avoids it (detached, `CREATE_NO_WINDOW`, `SW_HIDE`, a detached h9kd with `CreateNoWindow` children all keep it), and the modes that avoid it put a window on screen per child. One start mode I did not measure would avoid both: running h9kd on a desktop of its own with a visible console, so the windows exist and nobody sees them (the stand-in arms on a private desktop show 0 kept). It is untested against what h9kd's children need from a desktop (a credential manager or browser prompt on an unseen desktop would hang). The pushed branch's test is a Windows-only, opt-in test that records that a detached start leaves as many tokens as today's start (+1,069 against +826 over 1,200 launches against a null of +1,008), not one showing a detached start leaving none. I could not make that test, because it is false. The two weaknesses in the evidence are named in section 7: the live count does not reproduce the trace for detached sh (1.58 against 3.00) and cannot see gh's 1 a launch.
h9kd's node process was replaced at 09:10 by the node window (journal: node updated to 0.18.1 on Brian's word), not by this session; its identity at the start (pid 34684) and the end (pid 47288) therefore differ, and the document shows why.

## Proposed follow-ups (for the orchestrator to record in h9k; none is a task yet)

1. **Do not detach the children; keep today's start.** Detaching does nothing for the leak and breaks the agent-session wrapper and `gh`. Note this on the research findings.
2. **Spike: h9kd on a private desktop with a visible console** (`CreateDesktop`, `lpDesktop`, `CREATE_NEW_CONSOLE`, in `WindowsDaemonLaunch.Create`). The stand-in shows 0 kept for every program. Open: GUI prompts on an unseen desktop (Git Credential Manager is the system helper, `gh` the owner's override), browser opens by `gh auth login` or the agent CLI, conhost cost per child, whether the stop and status commands and the logon task still reach h9kd, and a run of the real daemon for an hour with the trace rate against today's 9 a second.
3. **Cut the launches that keep tokens, which the first research already priced** (credential helper chain 53.8%, `verify-commit` 18.5%): this spike changes nothing there, and neither detached nor `CreateNoWindow` removes any of those.
4. **Report to Microsoft** (the Feedback Hub draft in the research findings) with this spike's one-line reproduction: `probe spawn detached` against `probe nop`, no Git or MSYS2 code, with the visible-window result, which narrows the foreground-launch path.
