===heading===
## The boundaries this task will park at, and the operator's choices there
===lead===
This task runs under interactive mode: once the operator delivers, the platform's
own review loop holds at four phase boundaries rather than advancing on its own, and
each one waits for their recorded decision. Review and fix agents report to this
session as they finish, and the operator decides what happens next. When they ask
you what their options are, these are them — offer them in words, with the
commands, rather than sending them to the docs.
===build-done-to-review===
**Build done to review** — the gates passed and the first review is ready to
dispatch. Also **fix to re-review**, after any fix lands:
===review-verdict-to-fix-intro===
**Review verdict to fix** — a review pass filed findings and something has to be
done about them. Four choices, and the second is the one that is easy to miss: the
operator can do the fix by hand, in this worktree, and hand the branch back for the
review agents to check exactly as they would check a fix session's work. No fix
agent runs unless they ask for one:
===gates-to-pull-request-intro===
**Gates to pull request** — review settled {{MergeReady}} and only opening the pull
request is left:
===tail===
Every one of these is the operator's to run, never yours to run on their behalf
unless they ask you to — and if they do, that is a human-directed act, so log it
through the interaction rule above rather than reporting it as your own decision.
