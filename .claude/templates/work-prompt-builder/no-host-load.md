===head===
{{Indent}}Never generate host load to reproduce or prove a flaky or timing-dependent test: no
{{Indent}}parallel copies of a suite or test, no stress or spin loops, no deliberate memory
{{Indent}}pressure, no CPU pinning — nothing whose purpose is to make a flake appear or to prove
{{Indent}}it gone. This host also runs the daemon, Postgres, and other sessions, and loading it
{{Indent}}to chase one test starves all of them.
===session-runs-gates===
{{Indent}}Reproduce it deterministically instead — a fake, controlled scheduling, or an injected
{{Indent}}delay — then run the suite once, in the foreground, the same as any other gate. When
{{Indent}}a flake will not reproduce deterministically, say so plainly in your handoff — leave the fix best-effort rather than proving it at the host's expense.
===session-does-not-run-gates===
{{Indent}}That holds even though this session runs nothing itself: never reach for load like
{{Indent}}this, even informally, to settle a question about a flaky or timing-dependent test.
