> **RENUMBERED 16 -> 55 (2026-08-23): the number collided with 16-orchestrator-doc, which decision #68 cites as backlog 16. At refinement, reconcile this task's contract with IDEA-learning-capture (the canonical design): the two disagree on event names (Retired/Superseded vs Consolidated/Absorbed), scopes, and the corroboration gate - a merge, not a pick.**

---
project: hall9k
type: feature
objective: Record run-earned lessons as event-sourced platform data and inject each project's active lessons into every agent prompt, with a distillation run that shrinks the injected view without losing any history
criteria:
- h9k learn "<lesson>" records a LearningRecorded event on a new Learning stream (one stream per lesson), scoped to the project by default and to the owner with --owner; the bare positional form always writes and never reads
- Provenance is observed, never inferred - a lesson recorded from inside a run carries that run id and task id, obtained through whatever mechanism h9k ask already uses to identify its calling run; a lesson recorded by a human from a shell carries explicit nulls for both, plus the recording owner id
- h9k learn list, h9k learn show <id>, and h9k learn retire <id> --reason <why> are separate subcommands; retirement appends LearningRetired and never deletes, and h9k learn list --all still shows retired and superseded lessons
- AgentPromptBuilder injects the run's project-scoped active lessons plus the owner-scoped ones into every implementation, follow-up, retry, review, and review-fix prompt as one named section, each line prefixed with the lesson's short id so an agent can cite or retire it
- The injected section is bounded by DaemonOptions.MaxInjectedLessons (default 40) and DaemonOptions.MaxInjectedLessonCharacters, ordered distilled-first then most-recent-first; when the budget truncates, the section states how many active lessons it withheld and names h9k learn list, rather than cutting silently
- The lessons query is a cheap indexed filter over a LearningListItem projection (scope, scope id, status), with the value object converted to string before the Marten LINQ predicate per TASK-MODEL.md section 8
- h9k learn distill [--project <id>] creates an ordinary Research task through the existing add/publish/assign path, whose agent context is the project's active lessons and whose instructions are merge-and-cite only; no parallel dispatch path, no daemon-side distiller service
- A distillation records each merged lesson as a LearningRecorded carrying DistilledFrom ids and appends LearningSuperseded to every source lesson; the decider refuses a Distillation-sourced LearningRecorded whose DistilledFrom list is empty
- Nothing in the loop deletes - retired, superseded, and source lessons stay queryable, and h9k learn show <id> renders a distilled lesson's sources by id
- h9k status surfaces any project whose active lesson count exceeds MaxInjectedLessons, naming h9k learn distill as the lever; the daemon never distills on its own judgment
- The h9k learn --help tree teaches what a good lesson looks like (one claim, imperative, self-contained, no run-specific detail) with at least one realistic example, per the CLI command standards in AGENTS.md
- dotnet build and dotnet test pass
---
PLAN.md section 4 already promises this: "every run emits a summary, decisions made, and open
questions - that's the human's review surface and the memory for future tasks." Today the
memory half of that promise is unbuilt. What a run learns dies with its transcript, and the
same correction gets rediscovered by the next agent on the same project. The three standing
rules in AGENTS.md (authored history, origin incidents, never guess) exist because a human
noticed a repeated failure and wrote it down by hand; nothing in the platform does that job.

This task builds the loop as platform data rather than as gitignored markdown, which is the
whole point: lessons become queryable, auditable, replicated to every node that shares the
store, and survivable across any individual session.

Design constraints:
- One stream per lesson, not a growing tail on the Project stream. Project is a permanent
  configuration aggregate with two events; hanging an unbounded knowledge log on it would
  make every project read replay every lesson ever recorded. A per-lesson stream gives each
  lesson a UUIDv7 identity to cite, retire, and supersede, and matches the single-stream
  inline projection house rule.
- Retirement and supersession are different facts and both append. Retired means the lesson
  is no longer true or no longer needed, and carries a required reason. Superseded means the
  lesson was folded into a distilled lesson and points at it. Neither erases anything: the
  full history stays in the streams and only the injected view shrinks.
- Distillation must not invent. The distiller may only merge, rephrase, and drop lessons it
  was given; it may not add a claim absent from its sources, and every lesson it produces
  cites the source ids it came from. The decider enforces the citation (an empty
  DistilledFrom on a Distillation-sourced lesson is refused), so the honesty rule is a
  guard rather than a hope.
- Distillation reuses the task pipeline rather than adding a service. h9k learn distill is
  sugar that authors a Research task; the daemon claims, dispatches, and records tokens for
  it exactly as for any other work. Two things must be confirmed before building rather than
  assumed: how a Research task that produces no commits currently exits the pipeline (decision
  #28d exempts Research from the zero-commit failure but says nothing about PullRequestOpener),
  and whether a distillation task should be pinned to run alone. Do not open a pull request
  for a distillation run.
- Distillation is human-triggered, always. The daemon may observe that a project is over
  budget and say so in h9k status, and may never act on that observation. This is the
  decision-log #11 rule (the daemon never kills on its own judgment) applied to a second
  kind of judgment, and it matches the h9k pr resolve and h9k task retry levers, where the
  human asking is the grant.
- Truncation is announced. An injected view that silently drops lessons is a prompt that
  quietly lies about what the project knows, which is the never-guess rule inverted. When the
  budget bites, the section says what it withheld and where the rest lives.
- Prior art correction, worth carrying because our own notes had it wrong: beads (the
  reference implementation named in IDEA-learnings-loop) does the opposite of what that file
  assumed. Its semantic compaction is documented as "permanent graceful decay - original
  content is discarded." IDEA-learnings-loop credited beads with keeping full history while
  compacting the view; it does not. That property is ours to build, and it is the single
  largest difference between this design and the tool that inspired it. See BEADS-STUDY.md.
- Scope held deliberately narrow. Producers in this task are agents calling h9k learn mid-run
  and humans calling it from a shell. Two further producers named in IDEA-learnings-loop are
  follow-on work, not because they are unwanted but because each carries its own judgment
  problem: mining run summaries at closeout needs a rule for what in a summary is a lesson,
  and promoting recurring review findings needs a definition of recurring. Both get easier
  once there are real lessons to look at.
- Scope granularity beyond project and owner (lessons per task type or per persona, PLAN.md
  section 6.6) stays an open question. LearningScope is a closed-vocabulary value object per
  TASK-MODEL.md section 8, so a third scope is a static instance and a query clause, not a
  schema change.
- The funnel from lesson to enforcement stays open too. A lesson that keeps recurring
  ("agents keep skipping gate X") probably belongs in a verification profile rather than a
  prompt line, and prompt injection is the cheap first stop rather than the destination. Do
  not build promotion machinery here; do record the observation when the pattern appears.
- No dependency on backlog 05. If 05 lands first, h9k learn distill authors its Research task
  as a Draft and the same publish and assign steps apply, with no change to this design.
