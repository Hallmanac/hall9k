---
project: hall9k
type: feature
objective: The closeout sweep observes the merge of a pull request nothing is watching, so a Delivered row whose PR merged outside the watch set becomes Done by observation instead of sitting red forever
criteria:
- The closeout sweep, on the inspections it already makes, also considers tasks rendering Delivered with an open-or-unknown merge state and no run in the watch set; for each it asks GitHub for the pull request's actual state
- A pull request GitHub reports as merged gets its merge observed and recorded on the run stream exactly as a watched merge is, driving true closeout: Done, dependency release, and the Jira comment when a reference exists - the observation is dated when it was made, never backdated to when the merge happened (never-guess applies to time too)
- A pull request GitHub reports as closed without merging keeps the existing needs-you rendering; nothing is invented for it
- The sweep dispatches no agent for any of this - it is one API read per unwatched row, bounded by the number of such rows, and a row is inspected at the sweep's ordinary cadence, not hammered
- The six pre-monitor rows on the current board (PRs 8 through 12 era, tasks merged by hand before the closeout monitor existed) clear to Done on the first sweep after install, each carrying a real observation
- h9k status stops counting a row toward needs-you once its merge is observed, with no human act required
- dotnet build and dotnet test pass
---
Origin (2026-08-23): the three-surface board (decision #66) went honest about
merge observations, and its first render surfaced six Delivered rows reading
"the run ended without a merge being observed, and nothing is watching this
pull request any more" - all six from the pre-closeout-monitor era, their pull
requests merged by hand days ago and verified MERGED on GitHub. The display is
right and the record is genuinely incomplete; the gap is that nothing will ever
complete it, because observation only happens inside the watch set and these
runs left it by failing.

The rows' own lever (h9k pr resolve) is the wrong remedy at this scale: it
dispatches an agent follow-up per task, which is token spend to learn what one
API read answers. Observation is the platform's job; agents are for judgment
(decision #65's asymmetry, applied to closeout).

Scope note: this deliberately generalises past the six current rows - any
future run that dies after its PR opens leaves the same orphan, and the sweep
closes that hole permanently.
