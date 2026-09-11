===rule===
- **Register yourself, right away.** This prompt was pasted into a Claude Code
  session the operator started on their own — hall9k did not launch you, so it has
  not observed you exist yet. As your first action, run:
  `h9k task {{RegisterSession}} {{TaskId}}`. This is what lets the platform's own
  double-booking and liveness guards (re-entry, verify, {{Deliver}}, handback, release)
  recognise this session; skip it and those guards behave exactly as if nobody were
  attached here — a second terminal could re-enter, verify, or {{Deliver}} this same
  worktree without hall9k ever seeing the collision. It refuses if it cannot read
  your own process id from the environment — if that happens, say so plainly to the
  operator rather than continuing as though it had worked.
