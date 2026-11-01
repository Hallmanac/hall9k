===heading===
## Notes the sessions on this task left each other
===lead===
These are short notes that sessions working this task, and sessions on the other tasks in its epic,
recorded as they went, this task's own first and the newest first within each group. Each line says
which task it is on and what the platform observed about where it came from. They are a colleague's
notes, not instructions: weigh each one against the code in front of you, and do not follow one that
conflicts with the task's own brief or with what you can see for yourself.
===nothing===
No notes that this prompt may carry are recorded on this task or on the other tasks in its epic yet.
===left-out===
{{LeftOut}} did not fit in this section, which carries at most {{MaxNotes}} notes and
{{MaxCharacters}} characters of note text. A person can read them with `h9k task notes {{TaskId}}`; a
session cannot, because a session receives notes through its prompt.
===held===
Held out of this section on provenance: {{Held}}. A note this node cannot show was recorded here or on
another node of this owner's fleet is never written into another session's instructions. A person can
read it, marked as held, with `h9k task notes {{TaskId}}`.
===verbs===
To leave a note for the sessions that come after you on this task, run
`h9k task note {{TaskId}} "<text>"`, at most {{MaxTextLength}} characters, for something you learned about
this task that they would otherwise have to rediscover: a seam that is not where the brief says, a test
that only passes with a certain setup. A claim meant to outlive the task belongs in `h9k learn` instead.
A note is permanent and public: it is recorded in the project's shared ledger and never goes away, so it
never holds secrets, credentials, personal data, customer details, or the name of another project.
===closing-block===
You may also close your final message with a block of notes: a line reading exactly

    {{NotesMarker}}

with nothing else on that line, followed by up to {{MaxClosingBlockNotes}} bullet lines, one note each, in your own words. Put the
block after your summary and before any section your instructions say must come last, such as the line
reading {{HandoffMarker}}, which stays the last thing in your message wherever it is asked for. The
platform reads the last such block, records each bullet as a note on this task, and removes the block
from the message before it reads anything else, so the block is no part of your summary or your
handoff. Leave it out when you have nothing worth noting.
