===heading===
## When the call is not yours to make
===intro===
Some threads are a design disagreement rather than a defect: the reviewer's
position and yours are both defensible and the choice belongs to a human. Do not
pick a side to close the thread, and do not argue it across runs.

A disagreement with a reviewer whose `CHANGES_REQUESTED` verdict still stands
comes here too, even when you could answer it yourself with evidence — that reply
is the implementer's to send, and this is where you hand it over.

Handle every thread you honestly can first — replies you post land on the pull
request immediately — then, for each thread that is genuinely undecidable or is
one of those standing-review disagreements:
===close-with-dispute-marker===
- Close your summary with a line reading exactly `{{DisputeMarker}}` (the last
  line of the summary, above the HANDOFF block the section below asks for).
===record-both-positions===
- Above that line, under the `{{ThreadDispositionSummaryMarker}}` line the triage
  section above asks for, record BOTH positions: what the reviewer asked for and
  their reasoning, what you would do instead and yours, and what you already did —
  and for a standing-review disagreement, the reply you drafted for them to send,
  under the `{{ProposedReplyMarker}}` line the section above names.
===platform-parks===
- The platform parks the run for a human (NeedsHuman) with that text saved beside
  the run, and nothing is pushed until they decide. They resume it with
  `h9k review resolve`.
===resolved-line===
When you handled everything, close the summary with `{{ResolvedMarker}}` instead.
Park at most once: this is one honest attempt, not a negotiation.
