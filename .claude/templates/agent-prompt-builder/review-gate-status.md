===not-observed===
  No verification gates ran for this review: a pr-review task reads someone else's
  already-open pull request, and nothing here built or tested it. Whether it compiles
  or its tests pass is unobserved — judge the code as written, and say so plainly if a
  finding genuinely turns on it rather than treating either outcome as known.
===no-gates-configured===
  This project configures no verification gates, so there is no build of its own
  for you to reproduce; judge the code as written.
===gates-passed===
  The project's gates already ran and passed against this exact commit, immediately
  before this review was dispatched:
