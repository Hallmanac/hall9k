# Can hall9k stop the Windows console-launch token leak by starting its children without a console?

Spike (prototype); findings only. Nothing under `src/` changed. Times are EST, 2026-11-11, on the Windows node itself (Windows 11 Pro, build 26300), elevated session. Follows spike b0844e14, fix 54c44399 (#862) and research 6cf289d0 (#863).

## Answer in brief

1. **No.** Starting the child with `DETACHED_PROCESS` (stdio redirected) leaves exactly the same kept tokens per launch as today's start, for all four programs, by the kernel pool trace: git 7.00 against 7.00, gh 1.00 against 1.00, sh 3.00 against 3.00, `bin\bash.exe` 4.00 against 4.00; the agent CLI 21 against 22 a session (its own token is the one difference). Programs that start no child keep 0 in both modes.
2. **The leak does not need a console on any process in the chain.** In a chain where the spawner and its child were both started detached (nothing in it attached to a console) the spawner still keeps one token a launch (`probe spawn detached`, 40 of 40; matrix below). Git's own chain under a detached start (`git.exe`, its dispatcher, `git-remote-https.exe`) has no console at all and keeps 3 of 3.
3. **What does stop it is a visible console window in the chain**, not the absence of a console: in every arm where the creator, or the process created, was attached to a console with a visible window, no token was kept (0 of 1,200+ launches, five programs). A console whose window exists but is hidden (`SW_HIDE`), or has no window (`CREATE_NO_WINDOW`), keeps the token like any other. Each of those arms puts a console window on screen for every child, so none is usable on the owner's desktop.
4. **The detached start also breaks things**: `cmd.exe` started detached loses the output of anything it redirects (the agent session wrapper writes an empty file and exits 1); `gh`, started detached, gives each `tzutil` child a new console and a window appears; MSYS2 `sh` and `bash` re-attach to a parent's console or make their own.
5. **h9kd already has a console** (its own, windowless: `CREATE_NO_WINDOW`), under the logon task and under `h9k daemon start` alike. Starting it detached would stop the leak in the stand-in, but only because every console-subsystem child it then starts gets a new visible console window (one a launch); with `CreateNoWindow` added to those starts (a `src/` change) the leak comes back.

## What was run

BENCH_AND_RUN_PLACEHOLDER

## What was observed

OBSERVED_PLACEHOLDER

## Verdict

VERDICT_PLACEHOLDER
