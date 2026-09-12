===heading===
# Compose this task as a Jira card
===intro===
Work out what one card at {{Site}} should look like for the work below, then submit it
through Hall9k's own write surface. Composing the card is the whole job: you are not
implementing anything here, and you make no Jira call yourself — Hall9k is the sole
executor of every Jira write (Brian's design, 2026-08-28). Do not create, update, or
comment on anything in Jira directly, through MCP or otherwise: your job ends at a
composed payload, and hall9k validates it, executes it against Jira's REST API, and
verifies it.
===work-heading===
## The work
===acceptance-criteria-heading===
Acceptance criteria, as they stand on the task:
===context-heading===
## Context on the task
===where-it-goes-heading===
## Where it goes
===bound-to-board===
The project '{{ProjectName}}' is bound to board {{Board}}, so that is where the card belongs unless this repository's own rules say otherwise — and if they do, they win.
===no-board-bound===
No board is bound to the project '{{ProjectName}}'. Work out from this repository's own rules which project the card belongs in; if nothing says, stop and report that rather than picking one.
===routing-guidance===
The project's own routing guidance: {{RoutingGuidance}}
===no-modeling===
Hall9k models nothing about how a card should look. Issue type, required fields,
labels, components, parent links, and which board a piece of work is routed to are
this organisation's rules, not the platform's. Read them from the repository you are
in and follow them exactly as a person on this team would.
===repo-skills-heading===
This repo ships Claude skills; invoke the matching one rather than improvising:
===home-skills-heading===
The project home at {{HomeDirectory}} ships skills too, in its skills/ directory; read the SKILL.md of any that fits and follow it:
===project-links-heading===
Project links (fetch yourself as needed):
===reporting-back-heading===
## Reporting back (this is what finishes the run)
===payload-shape-intro===
Write your composed payload to a JSON file, shaped exactly like this:
===payload-example===
```json
{
  "workItemType": "Dev Task",
  "fields": {
    "summary": "...",
    "description": "...",
    "customfield_10401": "..."
  },
  "projectKey": "PROJ",
  "format": "markdown"
}
```
===payload-fields-explained===
"summary" and "description" both belong INSIDE "fields", never at the top level —
a top-level "description" is silently ignored, not an error. "summary" (inside
"fields") is mandatory for a create; use the customfield_* id a field's own metadata
reports for a custom field, never its display name. "projectKey" is optional, needed
only if this repository's own rules say a board other than the one named above;
"format" is optional ("markdown" or "plain" — default markdown). Write
the file outside this repository — a temp file (for example, one made with mktemp) —
never inside the working directory below: the working rules say not to modify
anything there, and another agent may be reading it at the same time. Then submit it
with exactly this:
===submit-command===
```
{{WriteCommand}} --op create --file <PATH-TO-YOUR-PAYLOAD.json>
```
===payload-not-existence===
Composing a payload is not the same as a card existing. That command validates it,
creates it against Jira's REST API, reads it back to verify, and records the result —
so if it refuses, the message says what was wrong; read it, fix the payload, and run
it again. If it reports the registered Jira connection is not authenticated, stop:
that is a handled state Hall9k retries on its own once a human refreshes the
connection's API token ('h9k connection add jira'), and you cannot fix it from here.
A run that never gets a verified key past that command has not published anything,
however the payload looked to you.
===run-in-foreground===
This session ends at your final message — nothing runs after it. Run that command in
the foreground and read its result before you finish: backgrounding it, or ending the
session before it returns, means nobody ever reads whether it succeeded.
===working-rules-heading===
## Working rules
===worktree-note===
- You are in {{WorkingDirectory}}, this project's own repository — not an isolated
  worktree. Read whatever you need. Do NOT modify files, commit, push, or open pull
  requests: another agent may be working in this repository right now.
===compose-once===
- Compose exactly one payload and submit it once. Hall9k itself refuses to file a
  second card for this task if an earlier attempt already created one, so a retried
  submission is safe — you do not need to search Jira for a duplicate yourself.
===card-audience===
- The card's audience is people, not agents. Write it the way this team writes cards;
  the operational detail above stays on the Hall9k task, which is what owns it.
===no-logging-invariant===
- The outside-interaction logging invariant every other dispatched prompt carries does
  NOT apply here: `h9k task log-interaction` records against a task's active run, and
  this task has none right now — it has not been claimed. If you interact with
  anything outside this session beyond the write-jira call above, say so plainly in
  your final summary instead.
===cannot-create-card===
- If you genuinely cannot create the card — no access, no rule saying where it goes,
  a required field nothing here answers — stop and say so plainly. Reporting that is a
  useful outcome; a card filed on a guess is not.
===closing-summary===
- End with a short summary: the key you created, where you filed it and why, and
  anything a human should check.
