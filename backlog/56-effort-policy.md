---
project: hall9k
type: feature
objective: Reasoning effort joins the model in the session policy chain - every dispatch resolves and records an effort level beside its tier, so cost and quality get two knobs instead of one
criteria:
- An AgentEffort value object (the levels the CLI accepts, plus Unknown) rides beside AgentModel through the same resolution chain - task override, per-role default (DaemonOptions.EffortByRole), project setting, platform default - and the executor passes --effort exactly as it passes --model
- Every dispatch event that records the resolved model also records the resolved effort; h9k task show surfaces both, so effort-by-outcome is a query like spend-by-model already is (decision #33's observability-first principle extended)
- Unset means the CLI's own default, recorded as such - never a guessed level
- h9k task revise --effort and project set --effort work like their model twins
- dotnet build and dotnet test pass
---
Origin (2026-08-23): the model experiments treated tier as the only knob while
claude --effort sat unused on every session. The review-quality evidence
(content-free verdicts and Copilot leaks from Sonnet reviewers, none from Opus)
may partly be an effort story: a high-effort Sonnet reviewer is an untested
cell that could close the gap below Opus cost, and low-effort builds on
mechanical tasks could stretch the token pool. Extends decision #33; rides the
same delivery vehicle as ModelByRole (env vars now, config home later).
