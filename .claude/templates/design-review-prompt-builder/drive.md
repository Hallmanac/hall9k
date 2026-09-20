===heading===
## Driving the product
===driven-lead===
This project has design-review driving turned on and a run skill on its ledger, so you are
expected to stand the product up on this checkout and use it. A design review that only read the
diff is judging markup; this one looks at the thing.
===driven-run-skill===
The run skill below is this project's own account of how it is stood up. Follow it. Do not invent
a command, a port, or a service it does not name; if it does not cover something you need, say so
in your report rather than guessing at it.
===driven-run-skill-missing===
This run decided at dispatch that this project had a run skill, and its text is not here: it was
replaced or removed between that decision and this session. Nothing is being withheld from you and
nothing has been reconstructed in its place. Read the repository for how it is stood up, say in
your report that you worked without the run skill and what you followed instead, and if you cannot
find a reliable way to start it, say that and read the change statically rather than guessing at a
command.
===driven-mechanics===
How to run it here without stranding anything:

- **An ephemeral port, every time.** Another session may be running this same project on this
  same machine right now. Bind port 0 where the stack allows it, or pick a high port and check it
  is free first; never take the project's own default port. Report the port you actually used.
- **Start it detached from your own turn, not with the harness's background tools.** Launch it
  the way a person would from a shell — a detached process, its output redirected to a file under
  this run's own directory — and then poll for readiness with ordinary foreground commands. The
  harness's `run_in_background`, `Monitor` and `ScheduleWakeup` are a different thing and are
  never the answer: your process is killed the moment your final message ends, so nothing they
  schedule ever fires.
- **Tear it down before you finish.** Kill the process tree you started, and say in your report
  that you did. A dev server left listening outlives this session and collides with the next one.
- **If it will not come up, say so and carry on.** A product that would not start is a fact worth
  reporting, in its own words, with what you tried. It is not a reason to abandon the review: read
  the change statically instead and mark your findings accordingly.
===driven-walk===
Then walk it. Drive the user-facing flows this change actually touched, through browser
automation, in the browser — the entry point, the happy path, the states you would expect a person
to hit. For each screen or flow you walk, take a screenshot, and cite that screenshot beside the
finding it supports. A finding drawn from the running product that cites nothing is indistinguishable
from one you reasoned your way to.
===driven-accessibility===
On every screen you walk, run an automated accessibility audit — the browser tooling's own
accessibility check, an axe-style pass, whatever this environment gives you — and report what it
said. Report it beside your own judgment, never instead of it: an automated audit catches
contrast, names, and roles, and it has nothing to say about whether the focus order makes sense
or whether the live region announces something useful. Both belong in the report, and a reader
must be able to tell which is which.
===driven-report===
Report each screen you walked as its own block:

    {{DrivenScreenMarker}} <the screen or flow, as a person would name it>
    {{ScreenshotMarker}} <path to the screenshot>
    {{AuditMarker}} <what the automated audit reported here, or that it found nothing>
    <what you found on this screen, with each screenshot cited beside the finding it supports>

Every lens still gets its own answer above and beyond these blocks: the blocks are the walk, the
lenses are the judgment.
===static-lead===
This review does not drive the product. {{WhyNotDriven}}, so you are reading the diff and this
repository's own design files — stylesheets, components, templates, tokens, whatever the change
touches — and nothing else.
===static-honesty===
Which means: do not start the application, and do not write a single sentence that reads as
though you had seen it running. No "the transition feels sluggish", no "the contrast is hard to
read on the live page". You can say what the markup and the styles will do; you cannot say what
it was like. The report states plainly at the top that this review was static, so a reader knows
exactly how much weight your findings carry, and that honesty is worth more than the extra
confidence.
===static-accessibility===
The accessibility lens in particular is a markup read here, not an audit: no automated check ran
on any running screen, so contrast, accessible names, roles and focus order are read from the
source. Report what the source shows and let the report mark it static; do not describe an audit
result you do not have.
