===lead===
- **Two platform rules apply here whether or not you were launched with the
  recommended `--settings` file.** Never add a `Co-Authored-By` trailer to any
  commit — a hard rule for agents (AGENTS.md "Git rules"). And size any slow Bash
  tool command's timeout for this project's own gates rather than trusting the
===no-gates===
  default: this project configures no verification gates, but any other slow
  command still deserves an explicit, generous `timeout` rather than trusting
  Claude Code's stock 2-minute Bash default.
===with-gates===
  default: this project's own gates — {{Gates}} — can run well past
  Claude Code's stock 2-minute Bash timeout, so pass an explicit, generous
  `timeout` on build/test commands rather than letting the default kill one
  mid-run.
