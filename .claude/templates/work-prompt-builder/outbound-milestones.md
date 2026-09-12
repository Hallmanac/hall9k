===heading===
## Reporting to the human (interactive mode)
===lead===
This task is worked under interactive mode: a human is the arbiter at each phase
boundary, and staying present means being told rather than polling. Your part is
judicious, not a running commentary — at most {{Count}} {{MessageWord}} for this {{PhaseLabel}} phase, one per
moment below, in order:
===milestone===
- **{{Milestone}}** — a one-line note, the moment it becomes true.
===milestone-final-head===
- **{{Milestone}}** — your last act before you end normally. Send the actual report
===milestone-final-body===
  (your closing summary, handoff, or verdict and findings — not just this
  label), then end. This send is in addition to, never instead of, your own
  final message: a tool call is never truly your last act, since the runtime
  forces one more assistant turn after any tool result, and that final message
  is the only text the platform ever reads back for a verdict, resolution, or
  handoff. Put the same report in your own final message too — closing with a
  line like "report sent" and nothing else discards it.
===parks-at-boundary-address-present===
  This task's interactive-mode phase-boundary park holds from there until
  the human's `h9k review proceed` or `h9k review resolve`, whether or not the send below actually lands.
===parks-at-boundary-no-address===
  This task's interactive-mode phase-boundary park holds from there until
  the human's `h9k review proceed` or `h9k review resolve` — see below for why there is no send to make on this run.
===does-not-park-address-present===
  Nothing supervises this run once you end: verification, delivery, and
  the review loop's own first boundary are a human's to trigger by hand
  with `h9k task {{Deliver}}`, not something that starts on its own the moment
  you finish, whether or not the send below actually lands.
===does-not-park-no-address===
  Nothing supervises this run once you end: verification, delivery, and
  the review loop's own first boundary are a human's to trigger by hand
  with `h9k task {{Deliver}}`, not something that starts on its own the moment
  you finish — see below for why there is no send to make on this run.
===address-present===
Address: `{{Address}}` — the human's own registered session, reached through the
cross-session mesh's SendMessage tool. Every milestone you send — whether it lands
or the session cannot be reached — is exactly the outside-interaction case the rule
above already commits you to logging: log each one there, so the record of what the
human was told lives on the run stream, not only in a transcript. A send that fails
(the session has ended, or SendMessage otherwise cannot reach it) is logged the same
way, rather than dropped silently, and never blocks you — keep working either way.
===no-address-delegated===
No registered human session is on record for this run right now. Unlike a fresh
headless build dispatch, this run is not necessarily new — `h9k task delegate`
reuses the operator's own existing interactive claim, so an earlier
`h9k task {{RegisterSession}}` against it is possible. Either way there is nothing
live to address: this contractor is only ever dispatched once any session recorded
as attached to this run is no longer alive, so a prior registration, if any, is
already stale. Skip sending these
===no-address-ordinary===
No registered human session is on record for this run right now — nobody has run
`h9k task {{RegisterSession}}` against it. That is the ordinary case for a fresh
headless dispatch under interactive mode (`h9k task start`, an ordinary dispatch
carrying the flag forward from an earlier `h9k task release --keep-interactive`,
or a retry, reopen, or follow-up redispatch) — each starts a new run, and no
registration carries forward from an earlier one yet. Skip sending these
===blank-address===
A human did register a session against this run (`h9k task {{RegisterSession}}` was
run), but that session carried no display name for SendMessage to address — there
is nowhere to send to, not nobody to send to. Skip sending these
===skip-milestones-parks===
milestones; the phase boundary still parks for the human's own proceed regardless.
===skip-milestones-does-not-park===
milestones; nothing parks here either — h9k task {{Deliver}} is still a human's to trigger by hand.
===log-once===
Log this once for the phase, not once per milestone, through the rule above.
