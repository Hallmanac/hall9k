===heading===
**track** — one more tag on every finding's header line, naming which review lens it
belongs to:
===example===
    {{FindingMarker}} {{SeverityTagKey}}=high; {{ScopeTagKey}}=in-scope; {{TrackTagKey}}=conformance; {{AtTagKey}}={{ExampleLocationPlaceholder}}
===body===
Use `{{TrackTagKey}}=conformance` or `{{TrackTagKey}}=adversarial` exactly. For a finding that reconfirms
or disputes a fix from the prior cycle's findings above, restate whichever track that
finding was already reported under. For a genuinely new finding — one the prior
findings never named — tag it by which question it answers: conformance if it is
about meeting the objective, the acceptance criteria, or repo doctrine; adversarial if
it is a defect regardless of what the work was asked to do. Leave the tag off only if
you genuinely cannot tell — the platform then counts the finding against every still-
active track rather than dropping it.
