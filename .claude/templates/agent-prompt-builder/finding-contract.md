===heading===
## How to report each finding (the platform parses this)
===intro===
Open every finding with a header line of exactly this shape, then write the finding
underneath it in prose:
===header-shape===
    {{FindingMarker}} {{SeverityTagKey}}=high; {{ScopeTagKey}}=in-scope; {{AtTagKey}}={{ExampleLocationPlaceholder}}
    {{DefectLabel}} one sentence saying what is wrong.
    {{ScenarioLabel}} the input or state that makes it misbehave, and what goes wrong.
===severity-heading===
**severity** — grade against these anchors, not against your own sense of importance:
===severity-anchors===
- `high` — a correctness, security, or data-integrity defect reachable in realistic use.
- `medium` — a real defect with bounded or unlikely impact.
- `low` — polish: phrasing, comment or doc-string wording, a doctrine or prose violation
  that misleads a reader without corrupting anything, a stale reference whether or not
  it misleads, or a style nit.
===severity-bar-foreign===
This review never judges the diff against this task's own acceptance criteria —
they describe the review deliverable, not the diff (stated above). Work that solves
a different problem than the pull request's own title and description state is never
`low` and never left ungraded: grade it `medium` at minimum.
===severity-bar-final-full-pass===
An unmet acceptance criterion, or work that solves a different problem than the one
stated, is never `low` and never left ungraded: grade it `medium` at minimum, same
as any other cycle. This is the mandatory final pass immediately before the pull
request opens, though, and its own bar for earning a fix cycle is narrower than an
earlier cycle's (Decisions Log #119): only a `high` finding, in-scope or out-of-scope,
costs a fix-and-re-review cycle here. An in-scope `medium` you grade is still recorded
and named on the pull request as a residual for the owner to see — grade against the
anchors above, never to force an outcome.
===severity-bar-ordinary===
An unmet acceptance criterion, or work that solves a different problem than the one
stated, is never `low` and never left ungraded: grade it `medium` at minimum. It
always meets the fix bar and must never be demoted into a ride-along.
===grade-exactly===
Use one of those three words exactly. A grade in any other word is one the platform
cannot read, and it counts as no grade at all rather than as the nearest word to it —
grade every finding you report; do not leave the tag off.
===fix-bar-foreign===
**The bar for {{NeedsFixesWord}}:** if every finding you have is graded low, or a grade you
could not confidently make, return {{MergeReadyWord}} and attach the finding anyway — do not
manufacture a {{NeedsFixesWord}} verdict to make sure it gets read. The platform still records
it either way — there is no fix-and-re-review cycle here for a {{NeedsFixesWord}} verdict
to cost, only the same findings report either verdict produces — so grade honestly
rather than picking whichever word you think matters more.
===fix-bar-final-full-pass===
**The bar for {{NeedsFixesWord}}:** if every finding you have is graded medium or low, or a
grade you could not confidently make, return {{MergeReadyWord}} and attach the finding anyway
rather than manufacturing a {{NeedsFixesWord}} verdict to make sure it gets read. The platform
still records it and decides on its own whether it is worth a session; on this mandatory
final pass, only a `high` finding, in-scope or out-of-scope, actually costs a
fix-and-re-review cycle (Decisions Log #119). An in-scope `medium` or `low` finding
here is recorded and carried onto the pull request as a residual instead. An
out-of-scope `medium` or `low` finding keeps the verdict {{NeedsFixesWord}} on its own and
still routes to its own draft task exactly as it would on any other cycle, but earns
no fix-and-re-review cycle by itself; an out-of-scope `high` is fixed directly in this
pull request instead, the same as an in-scope one.
===fix-bar-ordinary===
**The bar for {{NeedsFixesWord}}:** if every finding you have is graded low, or a grade you
could not confidently make, return {{MergeReadyWord}} and attach the finding anyway — do not
manufacture a {{NeedsFixesWord}} verdict to make sure it gets read. The platform still records
it and decides on its own whether it is worth a session; a {{NeedsFixesWord}} verdict costs a
whole fix-and-re-review cycle and is reserved for at least one medium or high finding.
===scope-heading===
**scope** — decide it against the diff, not against your judgment of whose problem it is:
===scope-anchors===
- `in-scope` — the defective line lives in code this branch added or changed.
- `out-of-scope` — the defect is pre-existing on `{{BaseBranch}}`; this diff only
  sits next to it. Check before you tag: the line is out of scope only if it is
  absent from `git diff {{ScopeBoundary}}...HEAD`.
===scope-report-foreign===
Report out-of-scope defects too — they are worth knowing about, and go into the same
findings report the owner walks by hand, who decides what to do with each one. Do
not stretch a tag either way: an in-scope defect tagged out-of-scope reads as less
this pull request's own problem than it is, and an out-of-scope one tagged in-scope
does the reverse.
===scope-report-ordinary===
Report out-of-scope defects — they are worth knowing about, and the platform routes the
smaller ones to their own bug tasks instead of growing this pull request. Do not stretch
a tag either way: an in-scope defect tagged out-of-scope leaves this branch broken, and
an out-of-scope one tagged in-scope drags unrelated work into the diff.
