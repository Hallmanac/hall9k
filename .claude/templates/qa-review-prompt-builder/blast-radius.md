===heading===
## Your first job: the blast-radius map
===intro===
Before you look for a single defect, map the blast radius. Everything else in this review hangs off it, and every finding you report later has to name the map entry it came from — a finding with no entry behind it is a finding you found by wandering, and the point of this review is that it does not wander.

Read the whole diff, then the code around it, and write down three groups of behaviour:
===groups===
1. **What the diff changes.** The behaviours a user or a caller gets differently because of these commits. Not the functions that were edited: the behaviours that moved. An edit that renames a local variable changes no behaviour and belongs in no group.
2. **What sits next to it.** The adjacent behaviours that share a code path, a query, a table, a cache, a queue, a configuration value, or a serialized shape with something in group one. These are the ones nobody thinks to re-test, and they are where regressions actually live. Find them by following the call graph out from each changed behaviour and by asking what else reads or writes the same data, not by guessing at what feels related.
3. **The user-facing flows that cross either.** The journeys a person actually takes that pass through anything in group one or group two, end to end, named the way a person would name them rather than the way the code does.
===plain-language===
Write the map in plain language, as the first section of your report. Somebody who has not read this diff should be able to read the map and understand what this change is near. Resist restating the diff: "the checkout total is recalculated after a coupon is removed" is a map entry; "CartService.Recalculate was edited" is not.
===entry-shape===
Every entry on the map, in all three groups, gets a header line of exactly this shape, then the behaviour in prose underneath it:

    {{MapMarker}} {{EntryTagKey}}={{ExampleEntry}}; {{CoverageTagKey}}={{CoveredWord}}
    The behaviour, in one or two plain sentences.
    What backs the verdict: see below.

`{{EntryTagKey}}` is a short label of your own choosing, unique within this report, that your findings cite later — `b1`, `b2`, `b3` reads fine. The label above is the example's own and the platform drops any entry carrying it, so do not use it for a real behaviour. Group the entries under plain headings for the three groups above: the headings are prose and the platform does not read them, so label them however reads best.
===verdict-heading===
**Every entry gets exactly one of three verdicts, and no entry may be left without one:**
===verdicts===
- `{{CoveredWord}}` — an automated test already exercises this. Name it: the file, and the test's own name. A test you believe exists but did not find is not this verdict; go and find it, and if you cannot, the entry is one of the other two.
- `{{NewTestWord}}` — nothing covers this, and an automated end-to-end test should. Specify it in a sentence: what it drives, and what it asserts. One sentence somebody could implement from, not "add coverage for the cart".
- `{{WalkThroughWord}}` — no automated test can honestly answer this, so a person has to walk it. Write out the steps: what they open, what they do, and what they should see at each step. Number them. A walk-through nobody could follow without asking you a question is not finished.
===no-gap===
An entry with no verdict is the one outcome this section exists to prevent, and the platform can see it: it reads every header line and reports an ungraded entry as an ungraded entry. If you genuinely cannot decide between two of the three, pick the more expensive one and say in the entry's prose why it was close.
