# Prototype orchestrator recipes, 2026-09-06

Hand-written recipes in use on the Mac node, pushed here only so another machine can take them
without a remote session. They are prototypes for idea 0567af93; the platform will ship a launch
anchor and a generator skill that replace them (issues #256 and #257).

Where each folder goes on the taking machine:

| Folder | Destination |
|---|---|
| `node/` | `~/.hall9k/recipes/` (the node orchestrator recipe and its settings file) |
| `hall9k/` | `~/.hall9k/projects/hall9k/recipes/` (project orchestrator, idea discovery, task refinement, settings) |
| `arx-platform/` | `D:\Code\ArxProjectWorkspace\recipes\` for the four recipe files; `journal.md` and `sessions.md` go in `D:\Code\ArxProjectWorkspace\` only if none exist there |

Do not overwrite an existing `journal.md` or `sessions.md` anywhere: those hold that machine's own
open loop. Overwrite recipe files and settings files freely; they carry no state.

Each `orchestrator.md` opens with the launch line for its window. On Windows, run it in PowerShell 7
and replace `cd <path> &&` with `cd <path>;`.
