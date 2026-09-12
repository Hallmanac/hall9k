===heading===
## Triage every thread before you fix anything
===intro===
Read every unresolved thread and the diff around it, then give each one exactly
one disposition before changing any code:
===fix-disposition===
- **fix** — the finding is real and in scope. The only disposition that earns a
  code change.
===decline-disposition===
- **decline** — you have reproduction-grade evidence it does not hold up: a
  scratch-repo demonstration, or a pointer to the code path that already handles
  it. Disagreeing is not evidence — if you cannot point to something concrete,
  this is not a decline.
===route-disposition===
- **route** — real, but out of this task's own scope. File it rather than growing
  this diff: `h9k idea add "<text>" --project "{{ProjectName}}"`.
===no-touch-until-triaged===
Do not touch code until every thread has a disposition. Apply fixes only for the
threads disposed fix. A triage where every thread is decline or route pushes
nothing — that is the honest outcome of this gate, not a failure to find work.
===close-with-blocks===
Close your summary with one block per thread, in this shape, so the platform can
record what you decided — this is for measurement only, and changes nothing about
the reply or the resolve you make in the thread itself (the section below covers
those):
===block-shape===
```
{{ThreadDispositionMarker}} {{ThreadTagKey}}={{ThreadIdPlaceholder}}; {{DispositionTagKey}}=fix|decline|route; {{KindTagKey}}=human|bot; {{AuthorTagKey}}=<login>
<why: the fix's brief restatement, the decline's evidence, or the route's scope reason>
```
===no-placeholder-echo-part1===
Write the actual thread's own node id there — never leave the `{{Part1}}
===no-placeholder-echo-part2===
{{Part2}}` placeholder text above in place, echoed back.
===block-ordering===
One block per thread, back to back with nothing between them, ahead of the
RESOLUTION line (if any) and the HANDOFF block. Put a line reading exactly
`{{ThreadDispositionSummaryMarker}}` right after the last block, before any recap
text, open questions, or dispute narrative — otherwise that prose is read as the
last thread's own evidence.
===kind-classification===
`{{KindTagKey}}=` reads the thread-starter the same way the closeout inspector's own
reviewer-kind classification does, off the GraphQL `__typename` the thread-fetch
already returns — `Bot` reads as `bot`; a `Mannequin` (GitHub's unclaimed-identity
placeholder — nobody is behind one) is neither, so leave `{{KindTagKey}}=` off rather than
counting it as human; every other type (`User`, an enterprise account) reads as
`human` — UNLESS the login is one of Copilot's own known accounts (`copilot` or
`copilot-pull-request-reviewer`, with or without a `[bot]` suffix), which reads
`bot` regardless of typename: the unified Copilot app has surfaced under both actor
types, and misreading it as a person would leave nobody to answer a thread you left
open for a human to close. That login check is the one named exception — never guess
`bot` from a login otherwise. Leave `{{KindTagKey}}=` off rather than guess if you never
fetched the typename at all.
