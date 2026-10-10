===session-runs-gates===
  Run this project's own build and test gates in the foreground and wait for them to
  finish before you rely on their result or move on. Never start one with the
  harness's own background tools — Bash's `run_in_background`, `Monitor`,
  `ScheduleWakeup`, or any other scheduled check-in — and never end your turn with
  one of those still pending: this session's process is killed the instant your final
  message ends, so a background task left running is left waiting on a notification
  that can never arrive, and the next thing to touch this worktree — another gate, or
  another session — starts while it is still writing to it. A command run with no
  explicit `timeout` only gets `BASH_DEFAULT_TIMEOUT_MS`, {{DefaultCeilingMinutes}} minutes
  today — request an explicit `timeout` up to the actual foreground ceiling,
  `BASH_MAX_TIMEOUT_MS`, {{ForegroundCeilingMinutes}} minutes today, sized so every gate
  this session itself runs fits inside one foreground run — a project's own host-coupled
  gate, if it has one, is never among them; the gate list above says so wherever it applies.
===session-does-not-run-gates===
  Never start anything with the harness's own background tools — Bash's
  `run_in_background`, `Monitor`, `ScheduleWakeup`, or any other scheduled check-in —
  and never end your turn with one of those still pending: this session's process is
  killed the instant your final message ends, so a background task left running is
  left waiting on a notification that can never arrive, and the next thing to touch
  this worktree — another gate, or another session — starts while it is still
  writing to it. A command run with no explicit `timeout` only gets
  `BASH_DEFAULT_TIMEOUT_MS`, {{DefaultCeilingMinutes}} minutes today — request an explicit
  `timeout` up to `BASH_MAX_TIMEOUT_MS`, {{ForegroundCeilingMinutes}} minutes today, in
  case anything you do run needs it.
===helper-process-exception===
  One exception, for a helper process this session itself needs, such as a dev server for a
  measurement or a browser check (never a build or test gate): start it with Bash
  `run_in_background` as the plain server command, with no `nohup`, no `( ... & )` subshell, no
  trailing `&`, no `setsid`, and no `disown`, and stop it before your final message with
  `TaskStop`, by the id `run_in_background` returned. If `TaskStop` is not in your tool list yet,
  load it through `ToolSearch` first (`select:TaskStop`). That is the one supported way to start
  and stop a helper process. Never stop any process by name, pattern, or port: no `pkill`,
  `killall`, `pgrep ... | xargs kill`, `kill $(lsof -t ...)`, `taskkill /IM`, or
  `Stop-Process -Name`, because those reach processes this session never started, the operator's
  own among them (a `pkill` once sent SIGTERM to about 64 of them). And never send a kill or
  cleanup command's stderr to /dev/null: a refusal or a wrong match is exactly what you need to see.
