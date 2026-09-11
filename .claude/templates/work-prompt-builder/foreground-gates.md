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
  `BASH_MAX_TIMEOUT_MS`, {{ForegroundCeilingMinutes}} minutes today, sized so this
  project's full verification suite fits inside one foreground run.
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
