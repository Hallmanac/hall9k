<!--
AGENTS.md's two standing-rule sections, verbatim, as they stood at commit cba329cb, which is
the commit this branch's own first commit sits on and the last one before those rules became
platform data (idea d805fd8b, piece 3). Named as a commit a fresh clone can actually read:
the fork point this snapshot was first taken at, 8ff66ce9, was rewritten out of existence
when the parent branch landed on main, so `git show` on it answers "bad object" to anybody
checking this file against its source. This file is the one-time import's frozen input and
its archive; nothing else reads it.
`LegacyStandingRulesParser` turns every bullet below into a `Decision` event whose legacy
id names this file's own section and the bullet's position in it.

One bullet from Working agreements is deliberately not carried in: the one instructing a
branch to append its own entry to PLAN.md §16 under a `PLACEHOLDER-<shortid>` token. This
change is what retires that convention, so importing it would record a rule that stopped
holding the moment it landed. Dropping it outright costs nothing, because nothing in this
repository cites an AGENTS.md rule by position. The §16 decision it restated, #162, is cited
thirteen times and cannot be dropped the same way: it is imported keeping its citation and
superseded in the same act, which `v0-decisions-log.md`'s own header records.

AGENTS.md's remaining sections stay authored in AGENTS.md: what the project is, how to
build and test it, the coding standards, the CLI command standards, and the repo skills
list. Those are conventions and navigation rather than rulings, and a convention that
lives beside the code it governs is easier to keep true than one behind a command.
-->

## Git rules

- **Commits are authored as the repo owner. No `Co-Authored-By` trailers, no bot attribution,
  no generated-with footers** (PLAN.md §6.6). This is a hard rule for agents.
- **Agents never START a review thread on a pull request; they only reply inside existing
  ones.** A thread's FIRST comment is always a reviewer's — including one where the author is the
  pull request's own owner leaving themselves a note — because every comment is authored under
  the human's login, and that is the only way the platform tells a reviewer's comment from an
  agent's. Open a new thread and the next run cannot tell your comment from feedback. Origin
  incident (2026-08-20): a human's own PR comment was structurally indistinguishable from an
  agent's reply under the same login. The honest long-term fix is node-signed authorship in the
  P2P identity layer (§16 #38-#58); until then, breaking this invariant breaks review handling
  (§16 #62). The one command that starts a thread, `h9k pr request-changes` (§16 #149), posts a
  review a human typed and ran; a lap's push guard denies an agent the ordinary route to it.
- **An agent never posts a decline or a route into a thread a *person* opened** — not the reply, not
  the resolve, whatever their verdict and however right it is. Draft it, park, and let the owner
  send, edit, or drop it (`h9k review resolve --post-reply-as-written` / `--post-reply "…"` /
  `--post-nothing`); replies route through `h9k pr reply`, which refuses the rest. A **fix**'s reply
  still posts in anyone's thread; a **bot's** thread is untouched (§16 #62, #152, #159). Origin: two
  accurate replies posted under Brian's login minutes after a reviewer approved, arx-platform
  PR #2021 (2026-09-09) and PR #2042 (2026-09-15), both deleted by hand.
- **Feedback reaches the platform only when a review is submitted.** GitHub hides an unsubmitted
  (`PENDING`) review's comments from the API entirely, so a reviewer part-way through a draft is
  invisible to the closeout monitor and to any agent reading the PR. Never read silence as "the
  reviewer had nothing to say".
- **Merging by hand is a four-gate check, and every gate is a reason not to merge** — the same four
  the daemon's pre-approved merge reads (§16 #135): CI green, the review decision satisfied, **no
  outstanding requested reviewer**, every review thread resolved. The third is a gate, not a
  formality, and `h9k status` / `h9k task show` name who (§16 #150). Detail: ORCHESTRATOR-WINDOW.md.
- Branch naming: `task/<id>-<slug>` unless the project set its own convention
  (`h9k project set <project> --branch-template`), created off `origin/main` with `--no-track` —
  except a task declared `--stacked-on` another, whose branch is cut from that parent's branch head
  and whose pull request targets it (Decisions Log #144). **Every base-branch reference in a
  dispatched session's own prompt is already the right one for that session**, stacked or not:
  never substitute `origin/main` for what the prompt names, and never retarget or rebase a stacked
  branch onto main by hand — the daemon does both mechanically when the parent merges.
- `main` is only ever checked out in the `dev/` worktree; agent worktrees are siblings of `dev/`.
- **PR branches are authored history, not a diary.** No work-in-progress commits, no "address
  review feedback" commits. A fix that belongs to an existing commit folds into it:
  `git commit --fixup=<owning-commit>`, then
  `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash origin/main` and
  `git push --force-with-lease`, verifying `git diff <old-tip> HEAD` is empty so a green test run
  carries over. Origin incident (2026-08-17, PR #6): a review-round fix first landed as its own
  commit and had to be rebuilt into the owning commits by hand.
- **An agent never pushes; the daemon pushes every branch — fresh or follow-up — with
  `git push --force-with-lease`, never plain `--force`.** Rewriting history per the rule above is
  safe: verify tree identity, finish, and let the platform push. The daemon's push runs an
  explicit ancestor-or-reflog check before pinning the lease's expected value, refusing outright
  rather than forcing over a tip it cannot account for (Decisions Log #26, #103, #104 — origin: a
  plain push once rejected two rebased follow-up branches and stranded completed work in 2026-08-17's
  first automatic follow-up runs).
- **Commit as you go during a fresh build session, then recompose once, right before you
  finish.** Checkpoint commits are crash protection, not authored history. Once the full suite is
  green and every checkpoint is committed, the session hunts its own diff for defects — a
  same-session adversarial self-review, capped at two rounds — then resets to the branch's fork
  point and recomposes the checkpoints into real history in one continuous step, verifying tree
  identity against the pre-reset tip before finishing. Every dispatched session's own prompt
  spells out the exact mechanics for that run (fork-point capture, the tree-identity check, the
  three mandatory self-review hunts); this is the standing rule behind it, not a substitute for
  it. Origin: Decisions Log #104, #113 — two full external review laps in one afternoon
  (2026-08-30) and three no-commit strandings in one night (2026-08-29) that this discipline now
  prevents.

## Working agreements

- Slice 1 before anything shiny; check SLICE-1.md before inventing work.
- **Standing rules carry their origin incident.** When a failure produces a new rule (in
  this file, the decisions log, or a skill), record the concrete incident that created it
  alongside the rule — so future readers know why it exists and when it might not apply.
  A rulebook is an accumulation of documented scars, not decrees.
- **Never guess at unobserved facts.** Audit fields, history, and identifiers record what
  was actually observed; the unobserved is represented as explicitly unknown (sentinels,
  nulls, honest labels like "purged per policy") — never plausibly filled in. An audit
  trail that guesses at provenance is worse than one that admits the gap.
- Every dependency or pattern choice gets a one-line "why" and a one-line "does this block
  the later vision?"
- A headless session runs its gates in the foreground, never behind
  `run_in_background`/`Monitor`/`ScheduleWakeup`, and never ends its turn with one still
  pending — it is killed the instant it finishes (PLAN.md §16 #167).
- **A dispatched session never generates host load to reproduce or prove a flaky or timing-dependent
  test** — no parallel copies of a suite or test, no stress or spin loops, no deliberate memory
  pressure, no CPU pinning, nothing whose purpose is to make a flake appear or prove it gone.
  Reproduce it deterministically instead (a fake, controlled scheduling, an injected delay), run the
  suite once in the foreground, or hand off honestly that it would not reproduce deterministically
  and leave the fix best-effort — this never forbids running this project's own gates, and adds no
  new gate, timeout, or setting. Origin: 2026-09-10 09:36 EDT, Windows, task 2c6e95f7, run 01a08b83
  — forty pwsh stress loops (4.4 GB, CPU pinned) starved h9kd and Postgres for seven minutes and
  forced a machine reboot (PLAN.md §16 #169).
