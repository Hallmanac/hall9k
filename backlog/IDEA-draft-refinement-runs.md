# Idea: refinement runs (agents working ON a draft, never FROM it)

Captured 2026-08-20 from Brian's conclusion while designing the Jira connection:
a task in Draft can have an agent dispatched to work on the task's own
definition - research, context gathering, criteria proposals - while remaining
strictly invisible to implementation dispatch.

## The rule that keeps Draft safe

Implementation runs build the task's objective and require the ready state -
that rule stays absolute (backlog 05: only Assigned/Queued tasks are claimable).
A REFINEMENT run works on the task's definition instead, and it is only ever
human-triggered: `h9k task refine <id> "<mission>"` dispatches an agent at a
specific draft deliberately. Drafts never enter any queue; nothing claims them.

## What a refinement run produces

Revisions and attachments, never code: it may propose objective wording and
acceptance criteria (recorded as a proposal for the human to accept via
TaskRevised, or applied directly - decide during design), attach findings with
provenance (IDEA-task-attachments: ContextAttached events, bytes in the task's
artifact directory), and end with a summary in the run transcript. Its worktree
needs are minimal or none (many refinement missions touch no repo at all).

## The motivating scenario

"Here is the Jira project URL - go see what's there and bring the right cards
in" during discovery: an agent surveys the board via MCP and the org's repo
skills, then creates draft tasks (or enriches this one) for the human to review
and publish. Judgment produces drafts; humans publish. Pairs with backlog 18's
read/write asymmetry doctrine and with the missing-auth conversation flow
(QuestionAsked when the agent lacks MCP or connection access).

## Dependencies

Backlog 05 (Draft exists) and realistically IDEA-task-attachments (the run's
findings need somewhere honest to land). Design after both.

## Generalizes to discovery runs (added 2026-08-20, backlog 22)

Ideas are first-class now (decision #35) and own a discovery workspace, so the
same shape applies one phase earlier: an agent dispatched at an IDEA, working on
the question "what is this?" rather than "how does this become executable?" -
surveying prior art, gathering files into the workspace, proposing a sharper
note. Same rule keeps it safe: ideas never enter any queue, and a discovery run
is only ever human-triggered (`h9k idea discover <id> "<mission>"` would be the
shape). What it produces is IdeaRevised plus files in the workspace, never code.
Explicitly out of scope in backlog 22, which built the concept and the CLI.
