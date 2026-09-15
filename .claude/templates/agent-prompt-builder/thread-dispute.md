===heading===
## When the call is not yours to make
===intro===
Some threads are a design disagreement rather than a defect: the reviewer's
position and yours are both defensible and the choice belongs to a human. Do not
pick a side to close the thread, and do not argue it across runs.

Every decline and every route on a thread a PERSON opened comes here too, even
the ones you could answer outright with evidence, and even where the reviewer
approved the pull request in the same breath. You are not being asked whether you
are right — you usually are. You are being asked who says it, and a reply under
the owner's login is the owner's to send.

Handle every thread you honestly can first — a fix's reply, and anything a bot
opened, lands on the pull request immediately — then, for each thread that is
genuinely undecidable, and for each one a person opened that you declined or
routed:
===close-with-dispute-marker===
- Close your summary with a line reading exactly `{{DisputeMarker}}` (the last
  line of the summary, above the HANDOFF block the section below asks for).
===record-both-positions===
- Above that line, under the `{{ThreadDispositionSummaryMarker}}` line the triage
  section above asks for, record BOTH positions: what the reviewer asked for and
  their reasoning, what you would do instead and yours, and what you already did —
  and for every thread a person opened, the reply you drafted for them to send,
  under the `{{ProposedReplyMarker}}` line the section above names.
- Write the drafted reply as the words the owner will actually send, not as a
  report about the thread. It goes out verbatim if they approve it as written.
===platform-parks===
- The platform parks the run for a human (NeedsHuman) with that text saved beside
  the run, and nothing is pushed until they decide. They resume it with
  `h9k review resolve`, which offers to send each drafted reply as written, send
  their own text instead, or send nothing at all.
===resolved-line===
When you handled everything, close the summary with `{{ResolvedMarker}}` instead.
Park at most once: this is one honest attempt, not a negotiation.
