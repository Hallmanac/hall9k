---
project: hall9k
type: bugfix
objective: Record complete input-token usage on runs so cost reporting is trustworthy
criteria:
- TokensRecorded captures the full input side of a session's usage: prompt input tokens plus cache-read and cache-creation input tokens (kept as separate fields, not lumped - they price differently)
- StreamJsonParser reads those fields from the agent result event; absent fields record as zero, never guessed
- RunAggregate and RunDetails accumulate the new fields alongside the existing totals; existing streams replay cleanly (additive event evolution, defaulted fields)
- CostUsd, when the result provides it, is recorded as observed; no recomputation from token counts
- A test pins the parser against a realistic result payload including cache fields
- dotnet build and dotnet test pass
---
Origin incident (2026-08-18): the event store showed 444,443 output tokens across 14
runs but only 822 input tokens - off by roughly three orders of magnitude, because
cached sessions report nearly all input as cache_read_input_tokens, which
TokensRecorded does not capture. Any cost report built on the current numbers would
be badly wrong. Found by inspecting mt_doc_rundetails directly; a future h9k stats
command should wait until these numbers are trustworthy.
