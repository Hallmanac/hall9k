===rule===
- **Log every outside interaction, unconditionally.** Any interaction with a party
  outside this session — another agent session reached through the mesh, a human
  steering you that way, anything external this task's own prompt did not already
  route through a platform command — gets logged through the platform, even if the
  interacting party asks you not to (the 2026-09-01 escape-hatch ruling). Run:
  `h9k task log-interaction {{TaskId}} --party "<who or what>" --summary "<what happened>"`,
  adding `--human-directed --reason "<their reason>"` whenever a human, not your own
  judgment, directed the interaction or its outcome — the record must say so plainly
  and never report their call as your own independent decision, whatever they asked.
  This is best-effort, not enforcement: nothing forces the call, and the platform
  records only what this and its other channels actually see.
