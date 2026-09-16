# Copilot review value on hall9k pull requests

Date: 2026-09-16. Scope: every hall9k pull request opened on Hallmanac/hall9k since 2026-09-01
that carried at least one GitHub Copilot review thread, through 2026-09-15 (the day before Brian
turned automatic Copilot review off for quota reasons). This report answers how much of what
Copilot caught the internal review had already seen, how much it genuinely missed, and at what
severity, so a replacement for Copilot review can be sized.

## Scope and data sources

Of 147 hall9k pull requests opened since 2026-09-01, 116 carried at least one Copilot review
thread; the other 31 carried none (Copilot review was not requested, or the change was too small
to draw a comment). Of those 116, 114 map to an `h9k` task branch with a local task record; two
(PR #235, `fix/git-noninteractive-knobs`, and PR #305, `docs/decisions-log-166-d2ded8d6`) are
hand-cut branches outside the task convention with no local task or internal-review record to
compare against. Their three Copilot threads are excluded from classification below rather than
guessed at; and this document only speaks for the 114 PRs and 309 threads that remain.

Sources used, matching the evidence the origin task named:

- Copilot review threads: `gh api graphql` against each pull request's `reviewThreads`, which
  groups each Copilot top-level comment with its replies and reports `isResolved` directly, rather
  than the flatter REST `pulls/<n>/comments` endpoint. The thread author reported by this API is
  `copilot-pull-request-reviewer`, matching the origin task's own description of the account.
- The daemon's own triage log line, `triaged N review thread(s) (x fix, y decline, z route)`, read
  from `/Users/brianhallmanac/.hall9k/h9kd.log` and `h9kd.log.1` (35 such lines total, 163 threads
  across 33 pull requests, reconciling to the 108 fixed / 31 declined / 24 routed the origin task
  already knew from a first pass over those two files). This line only exists for a pull request
  whose Copilot threads were triaged automatically, which the daemon began doing for pull requests
  opened from 2026-09-10 onward.
- For every other pull request (nearly all of them opened 2026-09-01 through 2026-09-09, before the
  daemon triaged Copilot threads on its own), the outcome is read from the fix session's own reply
  inside each thread, together with the thread's `isResolved` status. Every classification below
  that relies on this second source is marked so in the per-thread appendix's citation column
  wherever it matters; the two sources agree exactly on 30 of the 33 log-line pull requests. The
  other three, PR #317, PR #388, and PR #398, are addressed individually in the data-quality
  caveats below rather than forced to agree.
- The internal review's own findings: every `review-<n>-findings.md` file across every run under
  the task each pull request's branch belongs to (93 of the 114 tasks have a local record on this
  node; 863 such files in total, holding 3,143 individually parsed `FINDING:` lines with their
  severity, scope, `at=file:line`, and the platform's own "What the platform decided" disposition,
  fix-in-this-pull-request or ride-along). The other 21 tasks have no local record on this node;
  Hall9k is a local-first, multi-node platform, and a task can be built by a session on a different
  node whose task store this node never sees. Those 21 tasks' 35 fixed-outcome Copilot threads are
  reported as their own category, "no local record", rather than guessed into overlap or genuine
  miss.

### How overlap was judged

For every Copilot thread whose outcome was a fix, and whose task has a local findings record, this
report looked first for an internal finding at the same file within eight lines of the Copilot
thread's own `originalLine`. Every candidate that close was then read side by side with the
Copilot comment's own defect description to confirm it was the same defect, not two different
defects that happen to share a file. A spot check widened the match window to forty lines across
twenty-nine near-miss candidates; in every one of those, the closer reading showed a different
defect in the same file, which is why the eight-line window was kept rather than widened. Where a
match held, the internal finding's own disposition, fix-in-this-pull-request or ride-along, decided
whether the thread counted as overlap-fixed or overlap-ride-along. One further thread matched an
internal finding the review itself had routed out of scope rather than fixed or left as a
ride-along; it is reported separately as "overlap, internally routed" since it fits neither of the
task's two named overlap categories cleanly.

### Known data-quality caveats

- **PR #398** (`task/eec9096d`, opened 2026-09-15): all sixteen fix-session replies in this pull
  request's Copilot threads read as the literal string `@/tmp/pr398replies/tN.txt` instead of the
  reply text itself, evidence that whatever posted them used an `@file` argument a tool along that
  path did not expand. The daemon's own triage log line for this pull request (10 fix, 5 decline, 1
  route) is trustworthy; the sixteen individual thread outcomes are not recoverable from the thread
  text and are reported as "outcome unrecoverable" rather than guessed.
- **Three threads** (PR #199 twice, PR #229 once) show `isResolved: true` with no reply text
  anywhere, inline or at the pull request's issue-comment level. Whatever resolved them left no
  textual trace of why, so these three are also reported as "outcome unrecoverable".
- **PR #317 and PR #388** each carry two triage log lines from two separate follow-up runs whose
  combined counts (10 actions for #317, 6 for #388) exceed the number of Copilot threads currently
  visible on the pull request (5 and 3, respectively). The most likely explanation is a force-pushed
  rebase between the two runs retiring earlier threads whose diff anchors no longer resolve against
  the current head, a known GitHub behavior and not a defect in this report's own method. The
  per-thread and per-PR tables below report what is currently observable on the pull request, which
  is why the per-PR row for #317 and #388 will not sum to that pull request's own log-line total.

## Overall classification

| Category | Threads | Share |
|---|---:|---:|
| Overlap-fixed | 22 | 7.1% |
| Overlap-ride-along | 71 | 23.0% |
| Overlap-int.-routed | 1 | 0.3% |
| Genuine-miss | 83 | 26.9% |
| False-positive | 56 | 18.1% |
| Routed | 22 | 7.1% |
| No-local-record | 35 | 11.3% |
| Outcome-unrecoverable | 19 | 6.1% |
| **Total** | **309** | **100.0%** |

"Overlap-fixed" is a Copilot thread whose defect the internal review had already found and already
fixed in the same pull request; Copilot's comment carried no incremental value there beyond
confirming the fix. "Overlap-ride-along" is a Copilot thread whose defect the internal review had
already found, graded below High, and left unfixed as a ride-along per Decisions Log #87; Copilot's
fix session picked it up anyway. "Genuine-miss" is a Copilot thread whose defect does not match
anything in the internal review's own findings for that pull request's task, and which still got
fixed. "False-positive" is a Copilot thread the fix session declined with evidence in its reply.
"Routed" is a Copilot thread the fix session judged real but out of scope for the pull request in
hand, filed as a hall9k idea instead. "No-local-record" and "Outcome-unrecoverable" are the two
caveat categories above.

## Per pull request

| PR | Opened | Total | Overlap-fixed | Ride-along | Int.-routed | Genuine-miss | False-pos. | Routed | No-record | Outcome-unrecov. |
|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 120 | 2026-09-01 | 1 | 1 |  |  |  |  |  |  |  |
| 121 | 2026-09-01 | 2 |  |  |  | 1 | 1 |  |  |  |
| 131 | 2026-09-01 | 3 |  |  |  | 2 | 1 |  |  |  |
| 134 | 2026-09-01 | 2 |  |  |  | 1 | 1 |  |  |  |
| 136 | 2026-09-02 | 1 |  |  | 1 |  |  |  |  |  |
| 137 | 2026-09-02 | 1 |  |  |  | 1 |  |  |  |  |
| 139 | 2026-09-02 | 2 |  |  |  |  | 2 |  |  |  |
| 141 | 2026-09-02 | 1 |  |  |  |  | 1 |  |  |  |
| 142 | 2026-09-02 | 2 |  |  |  |  | 2 |  |  |  |
| 156 | 2026-09-02 | 3 |  |  |  | 1 | 2 |  |  |  |
| 157 | 2026-09-02 | 1 |  | 1 |  |  |  |  |  |  |
| 158 | 2026-09-02 | 1 |  |  |  |  | 1 |  |  |  |
| 159 | 2026-09-02 | 2 |  | 2 |  |  |  |  |  |  |
| 160 | 2026-09-02 | 4 | 1 | 2 |  | 1 |  |  |  |  |
| 161 | 2026-09-02 | 1 |  | 1 |  |  |  |  |  |  |
| 162 | 2026-09-03 | 2 |  | 1 |  | 1 |  |  |  |  |
| 163 | 2026-09-03 | 1 |  |  |  | 1 |  |  |  |  |
| 164 | 2026-09-03 | 1 |  | 1 |  |  |  |  |  |  |
| 165 | 2026-09-03 | 2 |  | 1 |  |  | 1 |  |  |  |
| 166 | 2026-09-03 | 2 |  | 1 |  | 1 |  |  |  |  |
| 167 | 2026-09-03 | 4 | 1 | 1 |  | 2 |  |  |  |  |
| 189 | 2026-09-03 | 1 |  |  |  | 1 |  |  |  |  |
| 190 | 2026-09-03 | 1 |  |  |  | 1 |  |  |  |  |
| 191 | 2026-09-04 | 1 |  |  |  | 1 |  |  |  |  |
| 192 | 2026-09-04 | 3 |  |  |  | 3 |  |  |  |  |
| 195 | 2026-09-04 | 2 | 1 | 1 |  |  |  |  |  |  |
| 196 | 2026-09-04 | 3 | 1 |  |  | 2 |  |  |  |  |
| 197 | 2026-09-04 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 199 | 2026-09-04 | 2 |  |  |  |  |  |  |  | 2 |
| 201 | 2026-09-04 | 2 |  |  |  | 1 | 1 |  |  |  |
| 204 | 2026-09-05 | 4 |  | 2 |  | 2 |  |  |  |  |
| 207 | 2026-09-05 | 2 |  | 2 |  |  |  |  |  |  |
| 209 | 2026-09-05 | 1 |  |  |  |  | 1 |  |  |  |
| 210 | 2026-09-05 | 1 | 1 |  |  |  |  |  |  |  |
| 211 | 2026-09-05 | 1 |  | 1 |  |  |  |  |  |  |
| 213 | 2026-09-05 | 2 | 1 | 1 |  |  |  |  |  |  |
| 214 | 2026-09-05 | 2 |  | 1 |  | 1 |  |  |  |  |
| 216 | 2026-09-05 | 3 | 1 | 2 |  |  |  |  |  |  |
| 217 | 2026-09-05 | 1 |  |  |  |  | 1 |  |  |  |
| 220 | 2026-09-05 | 1 |  |  |  |  | 1 |  |  |  |
| 221 | 2026-09-05 | 1 | 1 |  |  |  |  |  |  |  |
| 224 | 2026-09-05 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 228 | 2026-09-05 | 3 | 1 | 1 |  | 1 |  |  |  |  |
| 229 | 2026-09-05 | 1 |  |  |  |  |  |  |  | 1 |
| 234 | 2026-09-05 | 2 |  |  |  | 1 | 1 |  |  |  |
| 236 | 2026-09-05 | 1 |  | 1 |  |  |  |  |  |  |
| 237 | 2026-09-06 | 1 |  | 1 |  |  |  |  |  |  |
| 239 | 2026-09-06 | 1 |  |  |  | 1 |  |  |  |  |
| 240 | 2026-09-06 | 1 | 1 |  |  |  |  |  |  |  |
| 246 | 2026-09-06 | 2 |  | 1 |  | 1 |  |  |  |  |
| 255 | 2026-09-06 | 1 |  |  |  |  |  |  | 1 |  |
| 258 | 2026-09-07 | 2 |  |  |  |  |  |  | 2 |  |
| 260 | 2026-09-07 | 1 |  |  |  |  |  |  | 1 |  |
| 261 | 2026-09-07 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 262 | 2026-09-07 | 2 |  |  |  |  | 1 |  | 1 |  |
| 264 | 2026-09-07 | 2 |  |  |  |  |  |  | 2 |  |
| 268 | 2026-09-07 | 1 |  |  |  |  | 1 |  |  |  |
| 269 | 2026-09-07 | 2 |  |  |  |  | 2 |  |  |  |
| 271 | 2026-09-07 | 2 |  |  |  |  |  |  | 2 |  |
| 272 | 2026-09-07 | 1 |  |  |  |  |  |  | 1 |  |
| 273 | 2026-09-07 | 2 |  |  |  |  |  |  | 2 |  |
| 274 | 2026-09-07 | 1 |  |  |  |  | 1 |  |  |  |
| 275 | 2026-09-07 | 1 |  | 1 |  |  |  |  |  |  |
| 276 | 2026-09-07 | 3 |  |  |  |  |  |  | 3 |  |
| 277 | 2026-09-07 | 2 |  |  |  |  |  |  | 2 |  |
| 279 | 2026-09-08 | 3 |  | 2 |  |  | 1 |  |  |  |
| 285 | 2026-09-08 | 1 |  |  |  |  | 1 |  |  |  |
| 286 | 2026-09-08 | 2 |  |  |  |  |  |  | 2 |  |
| 289 | 2026-09-08 | 2 |  | 1 |  |  | 1 |  |  |  |
| 292 | 2026-09-09 | 2 |  |  |  |  | 1 |  | 1 |  |
| 293 | 2026-09-09 | 2 |  | 2 |  |  |  |  |  |  |
| 294 | 2026-09-09 | 1 |  |  |  |  |  |  | 1 |  |
| 295 | 2026-09-09 | 3 |  | 1 |  | 2 |  |  |  |  |
| 300 | 2026-09-09 | 1 |  |  |  |  | 1 |  |  |  |
| 302 | 2026-09-09 | 3 |  | 3 |  |  |  |  |  |  |
| 303 | 2026-09-09 | 3 |  |  |  |  | 1 |  | 2 |  |
| 304 | 2026-09-10 | 1 |  |  |  |  |  |  | 1 |  |
| 311 | 2026-09-10 | 2 |  | 2 |  |  |  |  |  |  |
| 312 | 2026-09-10 | 4 |  | 2 |  | 1 | 1 |  |  |  |
| 313 | 2026-09-11 | 2 |  |  |  |  |  |  | 2 |  |
| 316 | 2026-09-11 | 3 | 1 | 1 |  |  | 1 |  |  |  |
| 317 | 2026-09-11 | 5 | 1 | 1 |  | 2 | 1 |  |  |  |
| 318 | 2026-09-11 | 4 |  |  |  | 2 | 1 | 1 |  |  |
| 323 | 2026-09-11 | 3 |  | 2 |  |  | 1 |  |  |  |
| 325 | 2026-09-12 | 1 |  |  |  | 1 |  |  |  |  |
| 326 | 2026-09-12 | 2 |  |  |  |  |  | 2 |  |  |
| 330 | 2026-09-12 | 3 |  | 1 |  | 2 |  |  |  |  |
| 332 | 2026-09-12 | 1 |  |  |  | 1 |  |  |  |  |
| 334 | 2026-09-12 | 5 |  | 3 |  | 1 | 1 |  |  |  |
| 335 | 2026-09-12 | 4 |  | 1 |  | 1 | 2 |  |  |  |
| 336 | 2026-09-13 | 16 | 1 | 4 |  | 7 | 1 | 3 |  |  |
| 337 | 2026-09-13 | 3 |  | 2 |  | 1 |  |  |  |  |
| 338 | 2026-09-13 | 7 | 2 | 2 |  | 3 |  |  |  |  |
| 341 | 2026-09-13 | 3 |  |  |  |  | 1 |  | 2 |  |
| 342 | 2026-09-13 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 343 | 2026-09-13 | 8 |  |  |  | 2 |  | 6 |  |  |
| 344 | 2026-09-13 | 5 |  |  |  | 5 |  |  |  |  |
| 346 | 2026-09-13 | 2 |  | 1 |  | 1 |  |  |  |  |
| 348 | 2026-09-14 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 366 | 2026-09-14 | 9 | 2 |  |  | 4 |  | 3 |  |  |
| 370 | 2026-09-14 | 4 | 1 | 1 |  | 1 | 1 |  |  |  |
| 374 | 2026-09-14 | 3 | 1 | 1 |  |  | 1 |  |  |  |
| 375 | 2026-09-14 | 8 | 1 | 1 |  | 3 |  | 3 |  |  |
| 376 | 2026-09-14 | 4 |  |  |  |  | 1 |  | 3 |  |
| 379 | 2026-09-15 | 10 | 1 | 1 |  | 4 | 1 | 3 |  |  |
| 380 | 2026-09-15 | 2 |  | 2 |  |  |  |  |  |  |
| 382 | 2026-09-15 | 5 |  |  |  | 3 | 2 |  |  |  |
| 388 | 2026-09-15 | 3 |  |  |  | 1 | 2 |  |  |  |
| 392 | 2026-09-15 | 2 |  | 1 |  |  | 1 |  |  |  |
| 394 | 2026-09-15 | 3 |  | 1 |  |  | 1 | 1 |  |  |
| 396 | 2026-09-15 | 3 |  | 1 |  | 1 | 1 |  |  |  |
| 397 | 2026-09-15 | 4 |  |  |  |  |  |  | 4 |  |
| 398 | 2026-09-15 | 16 |  |  |  |  |  |  |  | 16 |
| 399 | 2026-09-15 | 3 |  |  |  | 1 | 2 |  |  |  |

## Genuine misses, in detail

Eighty-three Copilot threads across fifty pull requests matched nothing in the internal review's
own findings and still got fixed. Every one below carries a severity in the internal review's own
three-point vocabulary (assessed by this report using that same vocabulary, since the internal
review never saw these defects to grade them itself), whether the defect could have reached a user
of the merged code, and a class tag.

### Severity and reach

| Severity | Count | Share | Reached a user (Yes) | Reached a user (No) |
|---|---:|---:|---:|---:|
| high | 18 | 21.7% | 18 | 0 |
| medium | 31 | 37.3% | 27 | 4 |
| low | 34 | 41.0% | 15 | 19 |
| **Total** | **83** | **100.0%** | **60** | **23** |

Severity tracks reach almost exactly at the top: every one of the eighteen high-severity misses
could have reached a user of the merged code. Twenty-seven of the thirty-one medium misses could
have; four could not (test-coverage gaps and one internal doc-comment claim, none of which change
runtime behavior on their own). Reach is roughly even among the thirty-four low misses, since that
tier is dominated by wording, docs, and test-only findings that are visible to a reader but do not
change behavior.

### Class tags

| Class tag | Count |
|---|---:|
| test-only | 16 |
| unfenced-read-then-write | 16 |
| docs | 11 |
| misleading-message | 7 |
| efficiency | 3 |
| missing-validation | 3 |
| ignored-return-value | 3 |
| code-consistency | 2 |
| stale-read | 2 |
| silently-wrong-default | 2 |
| missing-feature | 2 |
| missing-import | 1 |
| unhandled-exception | 1 |
| unstable-comparison | 1 |
| inconsistent-validation | 1 |
| stale-precondition-check | 1 |
| incorrect-identity-resolution | 1 |
| incorrect-discriminator | 1 |
| leaked-child-process | 1 |
| unvalidated-input | 1 |
| stale-state | 1 |
| unhandled-edge-case | 1 |
| blocking-call-defeats-timeout | 1 |
| swallowed-exception | 1 |
| incorrect-state-classification | 1 |
| insufficient-validation | 1 |
| backward-compat-regression | 1 |

Sixteen of the eighty-three, the single largest class, are the same shape: an event-sourced stream
appended to without an `expectedVersion` fence, so a concurrent writer can silently lose or overwrite
the append. Six of those sixteen are graded high because the race sits on project archive, purge,
or reactivation, where the consequence is a live task or a whole project mutated after it should
have been protected. A further six graded high are each a different shape: an ignored fetch-result
return value ahead of a force-push (data loss), an unverified public key trusted for git-ledger
signing (identity integrity), a kill command that cannot reach a whole class of runs (an operator's
safety action silently failing), a cancelled operation whose child process keeps running and holding
locks, a synchronous call that defeats its own timeout and can hang a CLI command indefinitely, and
an upgrade path that breaks a working existing install. None of these sixteen "unfenced
read-then-write" instances, and none of the six other high-severity instances, are the kind of
defect a deterministic static analyzer finds; every one needs to know what the code is supposed to
guarantee, not just what it does.

### Full list

| PR | File:line | Severity | Reach | Class | Description |
|---:|---|---|---|---|---|
| 121 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2870` | low | No | efficiency | LifetimeReviewCycleCountAsync loads full RunDetails documents instead of just the ReviewCycle integers it needs |
| 131 | `src/Hall9k.Cli/Commands/TaskSetSessionCapCommand.cs:58` | low | Yes | misleading-message | Success message implies a live run exists when the command is deliberately state-agnostic |
| 131 | `src/Hall9k.Daemon/Dispatch/DispatchLoop.cs:78` | low | Yes | misleading-message | Operator-facing unset instruction names the wrong value (DescribeOrigin instead of the env var Source) |
| 134 | `tests/Hall9k.Tests/Fakes/RecordingProcessRunner.cs:100` | low | No | test-only | Test helper's exception message could echo a credential passed in test args into logs |
| 137 | `tests/Hall9k.Tests/Connectors/ClaudeSettingsFileTests.cs:50` | low | No | test-only | Test claims to pin a timeout value but only asserts a lower bound |
| 156 | `src/Hall9k.Connectors/Worktrees/GitWorktreeManager.cs:5` | low | No | missing-import | XML doc cref references a type with no using in scope, so the cref does not resolve |
| 160 | `src/Hall9k.Cli/Commands/AttentionComposer.cs:329` | low | Yes | misleading-message | Status wording claims a node-scoped watch that is not actually node-scoped, so it can read false from another node |
| 162 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:567` | low | No | efficiency | Loads every NodeDetails row to map node id to machine name instead of filtering to the ids actually displayed |
| 163 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:620` | low | Yes | misleading-message | Race-loss message overstates what was actually observed about the winning claim |
| 166 | `src/Hall9k.Cli/Commands/HeadlessLaunch.cs:110` | medium | Yes | unhandled-exception | Pidfile parse failure throws exception types the launch wrapper does not catch, crashing instead of giving recovery advice |
| 167 | `tests/Hall9k.Tests/Cli/TaskRegisterSessionCommandTests.cs:57` | low | No | test-only | Typo in a test method name |
| 167 | `tests/Hall9k.Tests/Connectors/WorkPromptBuilderTests.cs:16` | low | No | docs | Doc comment has broken hyphenation |
| 189 | `src/Hall9k.Daemon/Execution/PullRequestBody.cs:133` | low | Yes | docs | Grammatically broken text is rendered directly into the generated PR body |
| 190 | `src/Hall9k.Cli/Commands/TaskAddCommand.cs:131` | low | Yes | docs | CLI help placeholder does not show the documented 'default' value other commands show for the same setting |
| 191 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:1227` | low | No | code-consistency | Three separate versioned Append calls instead of one, though later analysis showed the two forms are equivalent under Marten |
| 192 | `src/Hall9k.Cli/Commands/TaskStartCommand.cs:340` | medium | Yes | misleading-message | Warning prints before SaveChangesAsync commits, so a lost race still shows a warning implying the start proceeded |
| 192 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:570` | medium | Yes | misleading-message | Same premature-warning pattern as the sibling TaskStartCommand thread, in TaskWorkCommand |
| 192 | `tests/Hall9k.Tests/Cli/TaskWorkClaimTests.cs:259` | low | No | test-only | Test swaps process-wide console state with no lock, risking flakiness under parallel execution |
| 196 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:60` | low | No | code-consistency | New optional parameter breaks the repo convention of keeping CancellationToken last |
| 196 | `src/Hall9k.Domain/Features/Run/Events/RunDispatched.cs:48` | low | No | docs | XML doc claims a sentinel value the implementation does not actually use |
| 197 | `tests/Hall9k.Tests/Domain/CrossProcessContainerGateTests.cs:52` | low | No | test-only | Test disposes an await-using variable a second time explicitly |
| 201 | `src/Hall9k.Cli/Infrastructure/CliCommandTree.cs:786` | low | Yes | docs | Help text omits that build/test byproduct directories inside src/tests also only warn |
| 204 | `src/Hall9k.Domain/Features/Run/ReviewSeverity.cs:40` | low | No | docs | Doc comment wording will diverge once the acceptance criteria's severity-anchor wording change lands |
| 204 | `tests/Hall9k.Tests/Daemon/AgentPromptBuilderTests.cs:1047` | low | No | test-only | Test pins wording that the acceptance criteria are about to change |
| 214 | `tests/Hall9k.Tests/Daemon/ReviewVerdictValidationTests.cs:1392` | low | No | test-only | No regression test for the 'so' intensifier denial case |
| 224 | `src/Hall9k.Domain/Features/Tasks/Handlers/TaskDecider.cs:378` | low | Yes | misleading-message | Refusal message names only one of two flags when both are supplied together |
| 228 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:1104` | medium | Yes | unstable-comparison | SequenceEqual on unsorted reviewer lists causes spurious events on harmless GitHub reordering |
| 234 | `tests/Hall9k.Tests/Integration/RunSupervisorTests.cs:975` | low | No | test-only | Test helper ignores the injected DaemonOptions parameter it was given to exercise |
| 239 | `tests/Hall9k.Tests/Connectors/NonInteractiveGitTests.cs:35` | low | No | test-only | Env-var filter substring match is broader than the git-scoped keys it claims to strip |
| 246 | `docs/operations.md:645` | low | Yes | docs | Product name capitalization is inconsistent in one sentence of the operations doc |
| 261 | `src/Hall9k.Cli/Commands/OrchestratorMeasureCommand.cs:90` | medium | Yes | unfenced-read-then-write | Project-scoped settings append has no expectedVersion, so a concurrent settings change can be lost |
| 295 | `src/Hall9k.Daemon/ProcessManagement/ProcessManagerBase.cs:129` | low | No | test-only | Unguarded test-only event invocation could throw and abort process-tree cleanup |
| 295 | `src/Hall9k.Domain/Features/Run/Projections/RunDetails.cs:693` | low | Yes | efficiency | Legacy shim field keeps serializing indefinitely after being folded forward, bloating stored documents |
| 312 | `src/Hall9k.Domain/Infrastructure/Persistence/OperatingSettings.cs:99` | low | No | docs | Decisions Log placeholder citation left stale in three places despite the 'every citation rewritten' guarantee |
| 317 | `src/Hall9k.Daemon/AutoPrReview/AutoPrReviewEngine.cs:827` | medium | Yes | unfenced-read-then-write | Node-wide launch hold sampled before inspection, so a hold raised mid-sweep is missed by the immediate-launch path |
| 317 | `src/Hall9k.Daemon/Execution/LaunchHoldEngine.cs:384` | medium | Yes | stale-read | Held-run probe keeps selecting a run that can never be resumed, so the hold never clears on that path |
| 318 | `src/Hall9k.Cli/Commands/InstallCommand.cs:521` | medium | Yes | missing-validation | Install validation accepts an incomplete template release; the failure only surfaces later at template-load time |
| 318 | `tests/Hall9k.Tests/Daemon/PromptTemplateContractTests.cs:23` | medium | No | test-only | Contract test silently passes when the checked-in template directory is entirely missing |
| 325 | `tests/Hall9k.Tests/Daemon/AgentPromptBuilderGoldenTests.cs:246` | medium | Yes | test-only | Golden fixture teaches a duplicated CLI flag that the real production caller never passes |
| 330 | `src/Hall9k.Domain/Features/Tasks/Projections/TaskListItem.cs:377` | medium | Yes | test-only | New test replays only the aggregate, not the inline projection the dispatcher actually reads |
| 330 | `docs/operations.md:791` | low | Yes | docs | Doc text contradicts the very claim table below it about when a task becomes claimable again |
| 332 | `tests/Hall9k.Tests/Domain/ProjectDeciderTests.cs:45` | medium | No | test-only | Test claiming to cover legacy deserialization actually only exercises the current code's own default |
| 334 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:5791` | high | Yes | unfenced-read-then-write | An abandoned task can still push a branch and open a real pull request before the abandon is honored |
| 335 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskPassageQuery.cs:549` | medium | Yes | silently-wrong-default | Null gate duration (unobserved) is mapped to zero, silently undercounting mixed old/new verification data |
| 336 | `src/Hall9k.Cli/Commands/ProjectAddCommand.cs:335` | high | Yes | unfenced-read-then-write | Archived-project reactivation appends with no stream-version fence, racing concurrent rename/reactivate/archive |
| 336 | `src/Hall9k.Cli/Commands/ProjectReactivateCommand.cs:46` | high | Yes | unfenced-read-then-write | Same unfenced reactivation-append pattern in the sibling reactivate command |
| 336 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:80` | high | Yes | unfenced-read-then-write | Task-state snapshot for project removal is not fenced against a concurrent assign, breaking the refusal guarantee |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:405` | high | Yes | unfenced-read-then-write | Archive check is not carried through the slow inspection path, letting closeout mutate an archived project's work |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:651` | high | Yes | unfenced-read-then-write | Archive guard sits after a branch that can already append RunSuperseded for an archived project |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:752` | high | Yes | unfenced-read-then-write | Same late-archive-guard pattern at a third closeout call site |
| 336 | `src/Hall9k.Cli/Commands/ProjectNameUniqueness.cs:26` | medium | Yes | inconsistent-validation | Uniqueness check compares names case-sensitively while rename/resolution treat names case-insensitively |
| 337 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:947` | medium | Yes | stale-precondition-check | Merge-state check is not repeated immediately before dispatch, so a merge during the gap still triggers a wasted re-review |
| 338 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:142` | high | Yes | unfenced-read-then-write | Purge reuses a pre-prompt task snapshot, letting the daemon act on a task whose stream is about to be deleted |
| 338 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:255` | high | Yes | unfenced-read-then-write | Purge scheduling has no expected-version fence against a task or project change during the confirmation prompt |
| 338 | `src/Hall9k.Domain/Features/Tasks/Handlers/TaskDependencyResolver.cs:156` | high | Yes | unfenced-read-then-write | Unfenced append can target a task stream a concurrent purge just hard-deleted, violating the hard-delete guarantee |
| 342 | `src/Hall9k.Cli/Diagnostics/ToolDoctor.cs:204` | medium | Yes | test-only | Missing-gh acceptance path has no test coverage; a regression could drop install guidance unnoticed |
| 343 | `src/Hall9k.Daemon/Review/PrReviewEngine.cs:983` | medium | Yes | unfenced-read-then-write | Kill-command terminal check does not protect the conformance dispatch path from a race |
| 343 | `src/Hall9k.Cli/Commands/RunKillCommand.cs:81` | high | Yes | incorrect-identity-resolution | Sentinel daemon-dispatched runs cannot be killed from their own machine because the wrong node id field is checked |
| 344 | `src/Hall9k.Cli/Commands/IdeaConcludeCommand.cs:45` | medium | Yes | unfenced-read-then-write | Idea-conclude append has no expected version, letting two concurrent terminal acts both land |
| 344 | `src/Hall9k.Cli/Commands/TaskAddCommand.cs:548` | medium | Yes | unfenced-read-then-write | Task-add append against a source idea is unfenced against a concurrent idea conclude/archive |
| 344 | `src/Hall9k.Cli/Commands/IdeaShowCommand.cs:64` | medium | Yes | stale-read | Outcome rendering reads a stale projection instead of the fresh aggregate it just computed, showing missing data until backfill |
| 344 | `claude/skills/orchestrator-recipe-generator/SKILL.md:482` | low | Yes | docs | Recipe example command omits a required positional argument, so copying it fails |
| 344 | `docs/concepts.md:94` | low | Yes | docs | Same missing-required-argument example in a second doc |
| 346 | `src/Hall9k.Domain/Features/Tasks/TaskRank.cs:69` | medium | Yes | incorrect-discriminator | A clean-start retry is misclassified as a first claim instead of the RetryOrHandback tier, skewing queue rank |
| 348 | `src/Hall9k.Connectors/Ledger/GitLedger.cs:356` | high | Yes | leaked-child-process | Cancellation does not terminate the underlying git process, which can keep running and holding repository locks |
| 366 | `src/Hall9k.Connectors/Identity/NodeKeyStore.cs:97` | high | Yes | missing-validation | An existing public-key file is trusted without verifying it derives from the private key it is paired with |
| 366 | `src/Hall9k.Cli/Commands/OwnerShowCommand.cs:80` | low | Yes | missing-feature | Command omits the node's public key from its output despite the stated requirement to show it |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:308` | high | Yes | ignored-return-value | A write conflict outcome is silently treated as false/success, letting a stale identity claim commit |
| 366 | `src/Hall9k.Cli/Commands/StatusCommand.cs:304` | low | Yes | missing-feature | Same missing public-key display on a second command surface |
| 370 | `src/Hall9k.Domain/Infrastructure/Persistence/EventOriginStampingListener.cs:68` | medium | Yes | silently-wrong-default | Pre-root-establishment events permanently record an empty owner root fingerprint, an invariant violation |
| 375 | `src/Hall9k.Connectors/Messaging/MessageInbox.cs:96` | high | Yes | unvalidated-input | Stream key built from an untrusted envelope field can misfile a message and cause a real message to be lost as a duplicate |
| 375 | `src/Hall9k.Domain/Features/Message/MessageEnvelopeCodec.cs:86` | medium | Yes | missing-validation | Required envelope fields are not validated before being accepted, letting malformed messages be stored as Parsed |
| 375 | `src/Hall9k.Connectors/Messaging/MessageInbox.cs:129` | medium | Yes | stale-state | A vouched sender's ignored mark is never cleared without a new envelope, leaving it stuck ignored |
| 379 | `src/Hall9k.Cli/Commands/MessageHandleCommand.cs:54` | medium | Yes | unfenced-read-then-write | Message-handle append is unfenced, letting two concurrent invocations both record MessageHandled |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:82` | medium | Yes | ignored-return-value | Fetch failure return value is ignored before a flush, letting it build and retry against a stale tip |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:161` | high | Yes | ignored-return-value | Fetch failure return value is ignored before a force-push, risking an overwrite of newer remote content |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:494` | medium | Yes | unhandled-edge-case | Zero-survivor squash never initializes the temporary index, so the normal all-aged-out path fails |
| 382 | `src/Hall9k.Domain/Infrastructure/Bootstrap/NodeBootstrap.cs:109` | high | Yes | blocking-call-defeats-timeout | A synchronous read before the timeout check means a hung gh process can block project add/join indefinitely |
| 382 | `src/Hall9k.Connectors/WorkItems/ProjectGitHubAccessMirror.cs:157` | medium | Yes | swallowed-exception | One malformed collaborator entry throws, and the surrounding catch discards every valid collaborator in the response |
| 382 | `src/Hall9k.Domain/Features/Project/Handlers/ProjectDecider.cs:531` | medium | Yes | incorrect-state-classification | An empty-but-successful first collaborator read is treated as unchanged, so the first-observation event never fires |
| 388 | `src/Hall9k.Daemon/Execution/PullRequestOpener.cs:141` | medium | No | test-only | No integration test drives the missing-parent-branch path through the real gh pipeline |
| 396 | `src/Hall9k.Daemon/Review/DecisionsLogRenumberer.cs:358` | medium | No | insufficient-validation | A backticked placeholder marker alone is not enough to prove mechanical assignment; a hand-authored note can reuse it |
| 399 | `src/Hall9k.Cli/Commands/TaskLinkIssueCommand.cs:70` | high | Yes | backward-compat-regression | An upgraded existing install with only the legacy account field now refuses a command that used to work |

## The three sizing questions

### What share a settle-time fix pass over leftover medium findings would have covered

Of the seventy-one overlap-ride-along threads, fourteen matched an internal finding graded medium;
the other fifty-seven matched one graded low. A settle-time fix pass scoped to leftover medium
findings, the natural next step up from today's fix-bar doctrine (Decisions Log #87), would have
covered those fourteen: 7.9% of the 177 Copilot-fixed threads this report could check against an
internal-findings baseline (the four overlap and genuine-miss categories combined), or 19.7% of the
ride-along overlap specifically. It would not touch the other fifty-seven low-severity ride-alongs,
which the platform's own doctrine already treats as acceptable unfixed residuals, and it would not
touch a single one of the eighty-three genuine misses, since a settle-time pass only revisits
findings the internal review already wrote down. Its marginal cost is close to zero: it reuses the
same fix-session lap the platform already runs, just triggered by a lower severity floor.

### What share a deterministic .NET analyzer set would have caught

Three of the eighty-three genuine misses, 3.6%, match a specific, nameable built-in .NET analyzer
rule under the repository's current build configuration:

- **CA1806** (Do not ignore method results): two instances, both in
  `GitLedgerMessageTransport.cs` (PR #379, lines 82 and 161), where `FetchRefAsync`'s boolean
  return value is discarded before a flush and before a force-push.
- **CA1031** (Do not catch general exception types): one instance, in
  `ProjectGitHubAccessMirror.cs:157` (PR #382), where a catch around an entire parse loop discards
  every valid collaborator because one malformed entry threw.

A fourth candidate, **CS1574** (XML comment has a `cref` that could not be resolved), would have
caught the broken-cref genuine miss on PR #156 (`GitWorktreeManager.cs:5`) and would also have
caught at least two of the low findings the internal review itself already wrote down (its very
first reviewed pull request in this sample, PR #311, carries exactly this class of finding twice).
It does not count toward the 3.6% above because `GenerateDocumentationFile` is off across this
repository today, so the compiler never evaluates the `cref` and CS1574 never fires; turning that
setting on is a one-line, zero-token change this report recommends regardless of anything else in
it, since it is the one place a genuine miss and two already-known internal findings share the
exact same fix.

No analyzer, built-in or otherwise, catches an unfenced `expectedVersion` append: that class needs
to know Marten's own concurrency contract and this codebase's own fencing convention, which is
exactly the sixteen-instance class that dominates the high-severity end of this list.

### What remains that only a second independent reviewer would catch

Eighty of the eighty-three genuine misses, 96.4%, are not caught by a deterministic analyzer and
are not covered by a settle-time medium fix pass, because the internal review never saw them at
all in the first place. This is the residual a Copilot replacement has to answer for: sixteen
unfenced-append races, six other high-severity defects each of a different shape, twenty-seven
further medium defects, and thirty-one low ones (mostly wording and test gaps, plus one already
counted toward the analyzer share). Closing this gap needs a reviewer capable of reading intent
against implementation, the same thing the internal review's own conformance and adversarial
lenses do; nothing mechanical stands in for that.

## Observed lap costs

Both sides of this decision are priced in the same currency the platform already spends. Reading
`h9kd.log`/`h9kd.log.1` for `agent session completed` lines paired with the triage line that
follows them, and for `fix run for cycle N completed` lines:

| Lap | Observed n | Avg input tokens | Avg output tokens |
|---|---:|---:|---:|
| Copilot-thread triage run (per pull request, 33 PRs, 163 threads) | 33 | 12,870,506 | 62,214 |
| Copilot-thread triage run (per run, some PRs triaged twice) | 35 | 12,135,048 | 58,659 |
| Internal review's own fix-run-per-cycle (this node, September) | 99 | 12,833,937 | 59,712 |

The three numbers land in the same band: roughly twelve to thirteen million input tokens (almost
entirely cache-read context carried forward) and fifty-nine to sixty-two thousand output tokens per
lap, regardless of whether the lap is triggered by Copilot's comments or by the internal review's
own findings. Handling Copilot's comments on a pull request costs about one ordinary fix-session
lap, not a new tier of expense.

## Recommendation

**Do all three, at different points, sized for what each one actually buys:**

1. **Ship the analyzer set in CI now.** It is free in this platform's own currency: a build-time
   check, not an agent session, so its marginal cost per pull request is effectively zero tokens
   and a few seconds of CI time. Enable `GenerateDocumentationFile` alongside it. Between the two,
   this closes four of the eighty-three genuine misses found here (3.6% before the doc-file flag,
   about 4.8% after) at a cost this report cannot even round up to one lap.

2. **Extend the fix bar's settle-time pass to leftover medium findings.** This is the second
   cheapest option and the only one of the three that spends the platform's existing machinery
   rather than adding a new one: one more fix-session lap, the same roughly twelve to thirteen
   million input and sixty thousand output tokens as any other lap already budgeted for, triggered
   only on the pull requests that actually carry a leftover medium finding (fourteen of the
   seventy-one observed in this sample's ride-along set, so most pull requests would not pay this
   cost at all).
   It buys 7.9% of the comparable Copilot-fixed value.

3. **Keep a second-vendor review lens at the final pass only, sized at roughly one lap per pull
   request.** The other two options together still leave 96.4% of the genuine misses uncaught,
   including sixteen unfenced-append races and six other high-severity defects, nearly all of which
   reached a user of the merged code. That is the load-bearing number here: most of what Copilot is
   worth on this repository is not redundant with the internal review and is not mechanically
   detectable, it is a second reader's judgment. Scoped to the final pass only, on the observed lap
   costs above, this option costs about one triage-run's worth of tokens per pull request, roughly
   twelve to thirteen million input and sixty thousand output tokens, the same order of magnitude
   the platform already spends on one fix cycle. That is not free, and at Copilot Pro+ quota
   exhaustion it is exactly the cost this report was asked to size, but it is not a new category of
   expense either: it is one more lap, on the pull requests that reach the final pass, not a
   token-hungry loop over every cycle.

Dropping Copilot without any of the three leaves the eighty-three genuine misses uncaught outright,
eighteen of them high severity and all eighteen reaching a user of the merged code in this sample.
Options 1 and 2 are worth doing regardless of what replaces Copilot, since they are close to free
and cost nothing extra to combine with option 3. Option 3 is the one that actually answers Brian's
original question: yes, most of what Copilot catches can be made up without a token-hungry loop,
by folding cheap wins into machinery the platform already runs, but the high-severity remainder
still needs a second reviewer, priced at about one ordinary lap per pull request, at the final pass
rather than on every cycle.

## Appendix: every classified thread

| PR | File:line | Category | Cited against |
|---:|---|---|---|
| 120 | `tests/Hall9k.Tests/Integration/TestSourceTree.cs:18` | Overlap-fixed | e8f31766-the-test-/01a05b1e/review-1-findings.md |
| 121 | `src/Hall9k.Daemon/Review/ReviewCapResolver.cs:91` | False-positive | 29 findings file(s) checked, e.g. e5c0d371-the-revie/01a05b77/review-1-findings.md |
| 121 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2870` | Genuine-miss | 29 findings file(s) checked, e.g. e5c0d371-the-revie/01a05b77/review-1-findings.md |
| 131 | `src/Hall9k.Cli/Commands/TaskSetSessionCapCommand.cs:58` | Genuine-miss | 13 findings file(s) checked, e.g. 0d3427cd-concurren/01a05a64/review-1-findings.md |
| 131 | `src/Hall9k.Daemon/Dispatch/DispatchLoop.cs:50` | False-positive | 0d3427cd-concurren/01a05cdc/review-1-findings.md |
| 131 | `src/Hall9k.Daemon/Dispatch/DispatchLoop.cs:78` | Genuine-miss | 13 findings file(s) checked, e.g. 0d3427cd-concurren/01a05a64/review-1-findings.md |
| 134 | `tests/Hall9k.Tests/Domain/ProcessTerminationGuardTests.cs:139` | False-positive | f773863f-the-test-/01a05e26/review-5-findings.md |
| 134 | `tests/Hall9k.Tests/Fakes/RecordingProcessRunner.cs:100` | Genuine-miss | 16 findings file(s) checked, e.g. f773863f-the-test-/01a05d80/review-1-findings.md |
| 136 | `src/Hall9k.Cli/Commands/StreamRenderer.cs:62` | Overlap-int.-routed | cd6de898-h9k-logs-/01a05f0e/review-3-findings.md |
| 137 | `tests/Hall9k.Tests/Connectors/ClaudeSettingsFileTests.cs:50` | Genuine-miss | 11 findings file(s) checked, e.g. a0d4e9ef-dispatche/01a05f85/review-1-findings.md |
| 139 | `src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs:1852` | False-positive | ca1e6728-the-revie/01a05f45/review-18-findings.md |
| 139 | `src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs:1857` | False-positive | 20 findings file(s) checked, e.g. ca1e6728-the-revie/01a05f45/review-1-findings.md |
| 141 | `src/Hall9k.Daemon/Execution/PullRequestBody.cs:152` | False-positive | d74c353d-a-mandato/01a061e6/review-1-findings.md |
| 142 | `src/Hall9k.Cli/Commands/ConfigShowCommand.cs:110` | False-positive | 13 findings file(s) checked, e.g. 3e340005-the-dispa/01a060b6/review-1-findings.md |
| 142 | `src/Hall9k.Cli/Commands/StatusCommand.cs:74` | False-positive | 3e340005-the-dispa/01a060b6/review-4-findings.md |
| 156 | `src/Hall9k.Connectors/Worktrees/GitWorktreeManager.cs:5` | Genuine-miss | 11 findings file(s) checked, e.g. 5de98032-a-project/01a05d61/review-1-findings.md |
| 156 | `src/Hall9k.Domain/Features/Project/Events/ProjectSettingsChanged.cs:29` | False-positive | 11 findings file(s) checked, e.g. 5de98032-a-project/01a05d61/review-1-findings.md |
| 156 | `src/Hall9k.Domain/Features/Tasks/ExternalReference.cs:34` | False-positive | 11 findings file(s) checked, e.g. 5de98032-a-project/01a05d61/review-1-findings.md |
| 157 | `src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs:720` | Overlap-ride-along | 113abb44-the-manda/01a063be/review-1-findings.md |
| 158 | `tests/Hall9k.Tests/Integration/CardPublicationEngineTests.cs:313` | False-positive | 15 findings file(s) checked, e.g. cd2ea1c7-jira-writ/01a061c3/review-1-findings.md |
| 159 | `AGENTS.md:165` | Overlap-ride-along | 43460c4b-agents-md/01a06386/review-1-findings.md |
| 159 | `docs/cli.md:118` | Overlap-ride-along | 43460c4b-agents-md/01a0642d/review-3-findings.md |
| 160 | `PLAN.md:880` | Overlap-ride-along | 5aa24264-a-pull-re/01a06365/review-5-findings.md |
| 160 | `src/Hall9k.Cli/Commands/AttentionComposer.cs:329` | Genuine-miss | 13 findings file(s) checked, e.g. 5aa24264-a-pull-re/01a06365/review-1-findings.md |
| 160 | `src/Hall9k.Cli/Commands/TaskPhaseComposer.cs:266` | Overlap-ride-along | 5aa24264-a-pull-re/01a06365/review-1-findings.md |
| 160 | `src/Hall9k.Cli/Commands/TaskResolveCommand.cs:183` | Overlap-fixed | 5aa24264-a-pull-re/01a06365/review-3-findings.md |
| 161 | `PLAN.md:847` | Overlap-ride-along | fe16b405-the-decis/01a06444/review-1-findings.md |
| 162 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:521` | Overlap-ride-along | 68a953b1-every-dis/01a0647e/review-3-findings.md |
| 162 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:567` | Genuine-miss | 6 findings file(s) checked, e.g. 68a953b1-every-dis/01a0647e/review-1-findings.md |
| 163 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:620` | Genuine-miss | 9 findings file(s) checked, e.g. 688a1ccf-h9k-task-/01a064d7/review-1-findings.md |
| 164 | `src/Hall9k.Cli/Commands/TaskLogInteractionCommand.cs:82` | Overlap-ride-along | 512caa36-every-out/01a06668/review-1-findings.md |
| 165 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:723` | False-positive | 864c7f30-re-enteri/01a065a9/review-3-findings.md |
| 165 | `tests/Hall9k.Tests/Cli/TaskWorkResumeArgumentsTests.cs:22` | Overlap-ride-along | 864c7f30-re-enteri/01a0662c/review-1-findings.md |
| 166 | `src/Hall9k.Cli/Commands/HeadlessLaunch.cs:110` | Genuine-miss | 20 findings file(s) checked, e.g. 8a56af78-a-deliber/01a065f5/review-1-findings.md |
| 166 | `src/Hall9k.Cli/Commands/TaskStartCommand.cs:307` | Overlap-ride-along | 8a56af78-a-deliber/01a066d6/review-1-findings.md |
| 167 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:231` | Overlap-fixed | 74a18e83-h9k-task-/01a06817/review-1-findings.md |
| 167 | `tests/Hall9k.Tests/Cli/InteractiveSessionLivenessTests.cs:175` | Overlap-ride-along | 74a18e83-h9k-task-/01a0688a/review-1-findings.md |
| 167 | `tests/Hall9k.Tests/Cli/TaskRegisterSessionCommandTests.cs:57` | Genuine-miss | 12 findings file(s) checked, e.g. 74a18e83-h9k-task-/01a066c6/review-1-findings.md |
| 167 | `tests/Hall9k.Tests/Connectors/WorkPromptBuilderTests.cs:16` | Genuine-miss | 12 findings file(s) checked, e.g. 74a18e83-h9k-task-/01a066c6/review-1-findings.md |
| 189 | `src/Hall9k.Daemon/Execution/PullRequestBody.cs:133` | Genuine-miss | 10 findings file(s) checked, e.g. 9b9e6988-fix-the-p/01a067b5/review-1-findings.md |
| 190 | `src/Hall9k.Cli/Commands/TaskAddCommand.cs:131` | Genuine-miss | 18 findings file(s) checked, e.g. 978ffa03-the-revie/01a06845/review-1-findings.md |
| 191 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:1227` | Genuine-miss | 9 findings file(s) checked, e.g. d20e69e0-re-land-t/01a06905/review-1-findings.md |
| 192 | `src/Hall9k.Cli/Commands/TaskStartCommand.cs:340` | Genuine-miss | 10 findings file(s) checked, e.g. 0ac72cb8-claiming-/01a069dc/review-1-findings.md |
| 192 | `src/Hall9k.Cli/Commands/TaskWorkCommand.cs:570` | Genuine-miss | 10 findings file(s) checked, e.g. 0ac72cb8-claiming-/01a069dc/review-1-findings.md |
| 192 | `tests/Hall9k.Tests/Cli/TaskWorkClaimTests.cs:259` | Genuine-miss | 10 findings file(s) checked, e.g. 0ac72cb8-claiming-/01a069dc/review-1-findings.md |
| 195 | `claude/skills/pr-summary/SKILL.md:47` | Overlap-fixed | f99153c9-the-canon/01a06cb3/review-1-findings.md |
| 195 | `claude/skills/pr-summary/SKILL.md:166` | Overlap-ride-along | f99153c9-the-canon/01a06d1f/review-3-findings.md |
| 196 | `src/Hall9k.Daemon/AutoPrReview/AutoPrReviewEngine.cs:453` | Overlap-fixed | c3bae83c-a-pull-re/01a06cae/review-1-findings.md |
| 196 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:60` | Genuine-miss | 24 findings file(s) checked, e.g. c3bae83c-a-pull-re/01a06c3b/review-1-findings.md |
| 196 | `src/Hall9k.Domain/Features/Run/Events/RunDispatched.cs:48` | Genuine-miss | 24 findings file(s) checked, e.g. c3bae83c-a-pull-re/01a06c3b/review-1-findings.md |
| 197 | `tests/Hall9k.Tests/Domain/CrossProcessContainerGateTests.cs:52` | Genuine-miss | 15 findings file(s) checked, e.g. e507c143-the-test-/01a06cf9/review-1-findings.md |
| 197 | `tests/Hall9k.Tests/Integration/CrossProcessContainerGate.cs:45` | Overlap-ride-along | e507c143-the-test-/01a06cf9/review-1-findings.md |
| 197 | `tests/Hall9k.Tests/Integration/PostgresFixture.cs:89` | False-positive | e507c143-the-test-/01a06cf9/review-3-findings.md |
| 199 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:2101` | Outcome-unrecoverable | 023f08bb-a-pull-re/01a06dd1/review-1-findings.md |
| 199 | `tests/Hall9k.Tests/Integration/CloseoutEngineTests.cs:1729` | Outcome-unrecoverable | 10 findings file(s) checked, e.g. 023f08bb-a-pull-re/01a06d28/review-1-findings.md |
| 201 | `src/Hall9k.Cli/Commands/TaskReleaseCommand.cs:221` | False-positive | 2 findings file(s) checked, e.g. fdd2cef3-fix-the-p/01a06ddd/review-1-findings.md |
| 201 | `src/Hall9k.Cli/Infrastructure/CliCommandTree.cs:786` | Genuine-miss | 2 findings file(s) checked, e.g. fdd2cef3-fix-the-p/01a06ddd/review-1-findings.md |
| 204 | `PLAN.md:923` | Overlap-ride-along | 3f54b196-move-the-/01a06ef4/review-3-findings.md |
| 204 | `src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs:1425` | Overlap-ride-along | 3f54b196-move-the-/01a06ef4/review-1-findings.md |
| 204 | `src/Hall9k.Domain/Features/Run/ReviewSeverity.cs:40` | Genuine-miss | 7 findings file(s) checked, e.g. 3f54b196-move-the-/01a06ef4/review-1-findings.md |
| 204 | `tests/Hall9k.Tests/Daemon/AgentPromptBuilderTests.cs:1047` | Genuine-miss | 7 findings file(s) checked, e.g. 3f54b196-move-the-/01a06ef4/review-1-findings.md |
| 207 | `src/Hall9k.Cli/Commands/TaskResolveCommand.cs:72` | Overlap-ride-along | 6f0bd22f-fix-the-p/01a06f52/review-3-findings.md |
| 207 | `src/Hall9k.Cli/Commands/TaskResolveCommand.cs:81` | Overlap-ride-along | 6f0bd22f-fix-the-p/01a06f52/review-1-findings.md |
| 209 | `tests/Hall9k.Tests/Integration/TaskAndIdeaIdResolverEmptyFragmentTests.cs:13` | False-positive | 2 findings file(s) checked, e.g. 4240a39a-fix-the-p/01a06f96/review-1-findings.md |
| 210 | `src/Hall9k.Cli/Commands/ReviewResolveCommand.cs:207` | Overlap-fixed | e5b48d64-fix-the-p/01a06fbb/review-1-findings.md |
| 211 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskDependencyQuery.cs:74` | Overlap-ride-along | e67cdbf5-fix-the-p/01a06fb8/review-3-findings.md |
| 213 | `src/Hall9k.Cli/Diagnostics/DatabaseDoctor.cs:359` | Overlap-ride-along | f33be620-fix-the-p/01a070a9/review-1-findings.md |
| 213 | `tests/Hall9k.Tests/Cli/DatabaseDoctorAlreadyRunningContainerTests.cs:81` | Overlap-fixed | f33be620-fix-the-p/01a06ff2/review-1-findings.md |
| 214 | `src/Hall9k.Daemon/Review/ReviewVerdictValidation.cs:808` | Overlap-ride-along | 29025f60-review-ve/01a07091/review-5-findings.md |
| 214 | `tests/Hall9k.Tests/Daemon/ReviewVerdictValidationTests.cs:1392` | Genuine-miss | 21 findings file(s) checked, e.g. 29025f60-review-ve/01a06f08/review-1-findings.md |
| 216 | `src/Hall9k.Cli/Commands/ProjectSetCommand.cs:581` | Overlap-ride-along | 0a328f2d-a-verify-/01a07056/review-3-findings.md |
| 216 | `src/Hall9k.Connectors/Verification/AdHocGateRunner.cs:113` | Overlap-fixed | 0a328f2d-a-verify-/01a07056/review-1-findings.md |
| 216 | `src/Hall9k.Connectors/Worktrees/CheckoutCleanliness.cs:86` | Overlap-ride-along | 0a328f2d-a-verify-/01a07056/review-3-findings.md |
| 217 | `src/Hall9k.Cli/DaemonControl/DaemonLifecycle.cs:163` | False-positive | 92da629d-h9k-daemo/01a070d8/review-3-findings.md |
| 220 | `src/Hall9k.Cli/Commands/InstallCommand.cs:101` | False-positive | f95118cb-a-locally/01a07107/review-1-findings.md |
| 221 | `tests/Hall9k.Tests/Integration/VerificationRunnerTests.cs:884` | Overlap-fixed | 1977570f-concurren/01a070d8/review-1-findings.md |
| 224 | `src/Hall9k.Cli/Commands/ReviewProceedCommand.cs:13` | False-positive | 9 findings file(s) checked, e.g. 83923630-interacti/01a07075/review-1-findings.md |
| 224 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:641` | Overlap-ride-along | 83923630-interacti/01a0718a/review-1-findings.md |
| 224 | `src/Hall9k.Domain/Features/Tasks/Handlers/TaskDecider.cs:378` | Genuine-miss | 9 findings file(s) checked, e.g. 83923630-interacti/01a07075/review-1-findings.md |
| 228 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:73` | Overlap-fixed | bbb061b8-a-task-ca/01a0723f/review-1-findings.md |
| 228 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:1104` | Genuine-miss | 7 findings file(s) checked, e.g. bbb061b8-a-task-ca/01a0711c/review-1-findings.md |
| 228 | `src/Hall9k.Domain/Features/Tasks/Events/TaskPreApprovedSet.cs:8` | Overlap-ride-along | bbb061b8-a-task-ca/01a0723f/review-3-findings.md |
| 229 | `claude/skills/commit-plan/SKILL.md:105` | Outcome-unrecoverable | ef2fefe5-the-commi/01a071cb/review-4-findings.md |
| 234 | `PLAN.md:937` | False-positive | 0dc06534-when-a-se/01a072bf/review-1-findings.md |
| 234 | `tests/Hall9k.Tests/Integration/RunSupervisorTests.cs:975` | Genuine-miss | 10 findings file(s) checked, e.g. 0dc06534-when-a-se/01a072bf/review-1-findings.md |
| 236 | `src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs:1005` | Overlap-ride-along | 33a161ac-agents-on/01a0739e/review-1-findings.md |
| 237 | `src/Hall9k.Cli/Commands/TaskDelegateCommand.cs:168` | Overlap-ride-along | 15f889e3-delegatin/01a07475/review-1-findings.md |
| 239 | `tests/Hall9k.Tests/Connectors/NonInteractiveGitTests.cs:35` | Genuine-miss | 1 findings file(s) checked, e.g. d4b15674-nonintera/01a075fd/review-1-findings.md |
| 240 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2013` | Overlap-fixed | 39aca95c-a-run-reb/01a0776c/review-1-findings.md |
| 246 | `AGENTS.md:552` | Overlap-ride-along | caa645e2-agents-md/01a077a1/review-1-findings.md |
| 246 | `docs/operations.md:645` | Genuine-miss | 8 findings file(s) checked, e.g. caa645e2-agents-md/01a0773a/review-1-findings.md |
| 255 | `src/Hall9k.Daemon/Dispatch/DispatchEngine.cs:387` | No-local-record | no local task record (task may have run on a different node) |
| 258 | `src/Hall9k.Cli/Commands/ProjectSetCommand.cs:61` | No-local-record | no local task record (task may have run on a different node) |
| 258 | `tests/Hall9k.Tests/Cli/ProjectCapSurfaceTests.cs:210` | No-local-record | no local task record (task may have run on a different node) |
| 260 | `src/Hall9k.Daemon/Dispatch/DispatchEngine.cs:1124` | No-local-record | no local task record (task may have run on a different node) |
| 261 | `src/Hall9k.Cli/Commands/OrchestratorLaunchTextSetCommand.cs:67` | Overlap-ride-along | e3b49a13-an-operat/01a07bb5/review-1-findings.md |
| 261 | `src/Hall9k.Cli/Commands/OrchestratorLaunchTextSetCommand.cs:77` | False-positive | e3b49a13-an-operat/01a07bb5/review-1-findings.md |
| 261 | `src/Hall9k.Cli/Commands/OrchestratorMeasureCommand.cs:90` | Genuine-miss | 6 findings file(s) checked, e.g. e3b49a13-an-operat/01a078f8/review-1-findings.md |
| 262 | `src/Hall9k.Cli/Commands/TrackerClaimCheck.cs:325` | No-local-record | no local task record (task may have run on a different node) |
| 262 | `src/Hall9k.Connectors/WorkItems/GitHubWorkItemProvider.cs:359` | False-positive | no local task record (task may have run on a different node) |
| 264 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:257` | No-local-record | no local task record (task may have run on a different node) |
| 264 | `tests/Hall9k.Tests/Domain/StackedBaseBranchGuardTests.cs:62` | No-local-record | no local task record (task may have run on a different node) |
| 268 | `tests/Hall9k.Tests/Integration/RunSupervisorTests.cs:1071` | False-positive | 3 findings file(s) checked, e.g. 1aeb73e7-runsuperv/01a077ee/review-1-findings.md |
| 269 | `src/Hall9k.Connectors/Worktrees/GitWorktreeManager.cs:289` | False-positive | 8 findings file(s) checked, e.g. a5d5662c-the-clean/01a07a37/review-1-findings.md |
| 269 | `src/Hall9k.Daemon/Execution/VerificationRunner.cs:1337` | False-positive | a5d5662c-the-clean/01a07a37/review-3-findings.md |
| 271 | `PLAN.md:1023` | No-local-record | no local task record (task may have run on a different node) |
| 271 | `src/Hall9k.Cli/Commands/PullRequestReviewVerdict.cs:237` | No-local-record | no local task record (task may have run on a different node) |
| 272 | `src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs:1578` | No-local-record | no local task record (task may have run on a different node) |
| 273 | `PLAN.md:1011` | No-local-record | no local task record (task may have run on a different node) |
| 273 | `claude/skills/hall9k-cli-reference/SKILL.md:135` | No-local-record | no local task record (task may have run on a different node) |
| 274 | `AGENTS.md:102` | False-positive | no local task record (task may have run on a different node) |
| 275 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:2049` | Overlap-ride-along | b750fad7-a-task-s-/01a07c95/review-3-findings.md |
| 276 | `src/Hall9k.Cli/Commands/TaskAddCommand.cs:487` | No-local-record | no local task record (task may have run on a different node) |
| 276 | `src/Hall9k.Cli/Commands/TaskRecordPublication.cs:93` | No-local-record | no local task record (task may have run on a different node) |
| 276 | `src/Hall9k.Cli/Commands/TaskRecordPublication.cs:189` | No-local-record | no local task record (task may have run on a different node) |
| 277 | `src/Hall9k.Daemon/Closeout/RemoteStackedParentSweep.cs:60` | No-local-record | no local task record (task may have run on a different node) |
| 277 | `src/Hall9k.Domain/Features/Tasks/Handlers/TaskDecider.cs:128` | No-local-record | no local task record (task may have run on a different node) |
| 279 | `docs/operations.md:901` | Overlap-ride-along | 65bbc1cd-a-do-now-/01a07f2b/review-3-findings.md |
| 279 | `src/Hall9k.Cli/Commands/AttentionComposer.cs:101` | Overlap-ride-along | 65bbc1cd-a-do-now-/01a07f2b/review-3-findings.md |
| 279 | `src/Hall9k.Cli/Commands/AttentionComposer.cs:110` | False-positive | 65bbc1cd-a-do-now-/01a07f2b/review-5-findings.md |
| 285 | `README.md:476` | False-positive | d9b2ddc2-an-operat/01a081a1/review-1-findings.md |
| 286 | `src/Hall9k.Daemon/Execution/GateInfrastructureFailureClassifier.cs:64` | No-local-record | no local task record (task may have run on a different node) |
| 286 | `tests/Hall9k.Tests/Cli/PublishTestSupport.cs:356` | No-local-record | no local task record (task may have run on a different node) |
| 289 | `src/Hall9k.Cli/Commands/TaskPhaseComposer.cs:553` | Overlap-ride-along | cc80655d-every-rev/01a08288/review-1-findings.md |
| 289 | `src/Hall9k.Domain/Features/Run/Events/ReviewThreadsTriaged.cs:2` | False-positive | cc80655d-every-rev/01a08356/review-3-findings.md |
| 292 | `src/Hall9k.Cli/Commands/StatusCommand.cs:211` | False-positive | no local task record (task may have run on a different node) |
| 292 | `src/Hall9k.Daemon/AutoPrReview/AutoPrReviewEngine.cs:546` | No-local-record | no local task record (task may have run on a different node) |
| 293 | `src/Hall9k.Daemon/Review/DecisionsLogRenumberer.cs:87` | Overlap-ride-along | 6df5f975-a-decisio/01a083fc/review-5-findings.md |
| 293 | `src/Hall9k.Daemon/Review/DecisionsLogRenumberer.cs:560` | Overlap-ride-along | 6df5f975-a-decisio/01a085fd/review-3-findings.md |
| 294 | `src/Hall9k.Domain/Features/Tasks/TaskAggregate.cs:1496` | No-local-record | no local task record (task may have run on a different node) |
| 295 | `src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs:1261` | Overlap-ride-along | 9a6d594d-a-headles/01a085b5/review-3-findings.md |
| 295 | `src/Hall9k.Daemon/ProcessManagement/ProcessManagerBase.cs:129` | Genuine-miss | 22 findings file(s) checked, e.g. 9a6d594d-a-headles/01a084c1/review-1-findings.md |
| 295 | `src/Hall9k.Domain/Features/Run/Projections/RunDetails.cs:693` | Genuine-miss | 22 findings file(s) checked, e.g. 9a6d594d-a-headles/01a084c1/review-1-findings.md |
| 300 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:888` | False-positive | no local task record (task may have run on a different node) |
| 302 | `PLAN.md:1258` | Overlap-ride-along | d2ded8d6-the-launc/01a087ce/review-3-findings.md |
| 302 | `README.md:466` | Overlap-ride-along | d2ded8d6-the-launc/01a087ce/review-3-findings.md |
| 302 | `src/Hall9k.Cli/Orchestrator/LaunchLineWriter.cs:22` | Overlap-ride-along | d2ded8d6-the-launc/01a088e2/review-1-findings.md |
| 303 | `src/Hall9k.Cli/Infrastructure/PostedProse.cs:48` | False-positive | no local task record (task may have run on a different node) |
| 303 | `src/Hall9k.Domain/Features/Project/WritingConventionsCheck.cs:229` | No-local-record | no local task record (task may have run on a different node) |
| 303 | `src/Hall9k.Domain/Features/Project/WritingConventionsCheck.cs:456` | No-local-record | no local task record (task may have run on a different node) |
| 304 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:1637` | No-local-record | no local task record (task may have run on a different node) |
| 311 | `src/Hall9k.Daemon/Execution/AgentPromptBuilder.cs:1967` | Overlap-ride-along | 18b7a833-a-dispatc/01a08bb6/review-1-findings.md |
| 311 | `tests/Hall9k.Tests/Domain/AgentsMarkdownLineCountTests.cs:34` | Overlap-ride-along | 18b7a833-a-dispatc/01a08bb6/review-1-findings.md |
| 312 | `PLAN.md:1302` | False-positive | 30ecf914-the-shipp/01a08c30/review-1-findings.md |
| 312 | `src/Hall9k.Cli/Commands/ConfigSetCommand.cs:135` | Overlap-ride-along | 30ecf914-the-shipp/01a08c30/review-1-findings.md |
| 312 | `src/Hall9k.Daemon/DaemonOptions.cs:299` | Overlap-ride-along | 30ecf914-the-shipp/01a08c30/review-3-findings.md |
| 312 | `src/Hall9k.Domain/Infrastructure/Persistence/OperatingSettings.cs:99` | Genuine-miss | 4 findings file(s) checked, e.g. 30ecf914-the-shipp/01a08c30/review-1-findings.md |
| 313 | `src/Hall9k.Connectors/Processes/ShareTolerantFile.cs:46` | No-local-record | no local task record (task may have run on a different node) |
| 313 | `src/Hall9k.Daemon/Execution/VerificationRunner.cs:1185` | No-local-record | no local task record (task may have run on a different node) |
| 316 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:886` | Overlap-fixed | 8a6bc1c1-a-pre-fin/01a08f99/review-1-findings.md |
| 316 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:1029` | False-positive | 9 findings file(s) checked, e.g. 8a6bc1c1-a-pre-fin/01a08eab/review-1-findings.md |
| 316 | `src/Hall9k.Domain/Features/Run/RunSessionLeg.cs:59` | Overlap-ride-along | 8a6bc1c1-a-pre-fin/01a08f99/review-3-findings.md |
| 317 | `src/Hall9k.Daemon/AutoPrReview/AutoPrReviewEngine.cs:827` | Genuine-miss | 12 findings file(s) checked, e.g. 1b24f59a-a-session/01a08e3c/review-1-findings.md |
| 317 | `src/Hall9k.Daemon/Dispatch/DispatchEngine.cs:474` | Overlap-fixed | 1b24f59a-a-session/01a08e3c/review-1-findings.md |
| 317 | `src/Hall9k.Daemon/Execution/LaunchHoldEngine.cs:252` | Overlap-ride-along | 1b24f59a-a-session/01a08e3c/review-6-findings.md |
| 317 | `src/Hall9k.Daemon/Execution/LaunchHoldEngine.cs:384` | Genuine-miss | 12 findings file(s) checked, e.g. 1b24f59a-a-session/01a08e3c/review-1-findings.md |
| 317 | `src/Hall9k.Daemon/Execution/LaunchHoldMonitor.cs:192` | False-positive | 1b24f59a-a-session/01a08fb5/review-1-findings.md |
| 318 | `AGENTS.md:22` | False-positive | 7 findings file(s) checked, e.g. 0989c44a-agent-pro/01a09172/review-1-findings.md |
| 318 | `src/Hall9k.Cli/Commands/InstallCommand.cs:521` | Genuine-miss | 7 findings file(s) checked, e.g. 0989c44a-agent-pro/01a09172/review-1-findings.md |
| 318 | `src/Hall9k.Daemon/Review/PromptContractTokens.cs:34` | Routed | 7 findings file(s) checked, e.g. 0989c44a-agent-pro/01a09172/review-1-findings.md |
| 318 | `tests/Hall9k.Tests/Daemon/PromptTemplateContractTests.cs:23` | Genuine-miss | 7 findings file(s) checked, e.g. 0989c44a-agent-pro/01a09172/review-1-findings.md |
| 323 | `docs/getting-started.md:46` | Overlap-ride-along | 48d5d9ea-the-readm/01a092e1/review-1-findings.md |
| 323 | `docs/getting-started.md:102` | False-positive | 48d5d9ea-the-readm/01a092e1/review-1-findings.md |
| 323 | `docs/getting-started.md:106` | Overlap-ride-along | 48d5d9ea-the-readm/01a092e1/review-1-findings.md |
| 325 | `tests/Hall9k.Tests/Daemon/AgentPromptBuilderGoldenTests.cs:246` | Genuine-miss | 6 findings file(s) checked, e.g. 6bb76ddf-agentprom/01a09285/review-1-findings.md |
| 326 | `src/Hall9k.Connectors/WorkItems/GitHubReviewAssignments.cs:264` | Routed | 11 findings file(s) checked, e.g. ed6044a5-a-mention/01a0927c/review-1-findings.md |
| 326 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:439` | Routed | ed6044a5-a-mention/01a09405/review-1-findings.md |
| 330 | `docs/operations.md:791` | Genuine-miss | 2 findings file(s) checked, e.g. 0f33676a-h9k-task-/01a095f2/review-1-findings.md |
| 330 | `src/Hall9k.Domain/Features/Tasks/Projections/TaskDetails.cs:593` | Overlap-ride-along | 0f33676a-h9k-task-/01a095f2/review-1-findings.md |
| 330 | `src/Hall9k.Domain/Features/Tasks/Projections/TaskListItem.cs:377` | Genuine-miss | 2 findings file(s) checked, e.g. 0f33676a-h9k-task-/01a095f2/review-1-findings.md |
| 332 | `tests/Hall9k.Tests/Domain/ProjectDeciderTests.cs:45` | Genuine-miss | 8 findings file(s) checked, e.g. f4cce412-a-newly-r/01a095f2/review-1-findings.md |
| 334 | `src/Hall9k.Cli/Commands/TaskAbandonCommand.cs:54` | False-positive | 05cc9000-abandonin/01a09674/review-1-findings.md |
| 334 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:94` | Overlap-ride-along | 05cc9000-abandonin/01a095f2/review-3-findings.md |
| 334 | `src/Hall9k.Daemon/Execution/RunSupervisor.cs:1730` | Overlap-ride-along | 05cc9000-abandonin/01a095f2/review-3-findings.md |
| 334 | `src/Hall9k.Daemon/Execution/VerificationRunner.cs:153` | Overlap-ride-along | 05cc9000-abandonin/01a09674/review-1-findings.md |
| 334 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:5791` | Genuine-miss | 6 findings file(s) checked, e.g. 05cc9000-abandonin/01a095f2/review-1-findings.md |
| 335 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskPassageQuery.cs:84` | False-positive | 10 findings file(s) checked, e.g. ae5afd1e-h9k-task-/01a0964d/review-1-findings.md |
| 335 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskPassageQuery.cs:385` | Overlap-ride-along | ae5afd1e-h9k-task-/01a09a24/review-3-findings.md |
| 335 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskPassageQuery.cs:503` | False-positive | 10 findings file(s) checked, e.g. ae5afd1e-h9k-task-/01a0964d/review-1-findings.md |
| 335 | `src/Hall9k.Domain/Features/Tasks/Queries/TaskPassageQuery.cs:549` | Genuine-miss | 10 findings file(s) checked, e.g. ae5afd1e-h9k-task-/01a0964d/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectAddCommand.cs:82` | Overlap-ride-along | 7228d4c7-a-project/01a098bc/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectAddCommand.cs:335` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectAddCommand.cs:361` | False-positive | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectNameUniqueness.cs:26` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectReactivateCommand.cs:46` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:35` | Overlap-fixed | 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:42` | Routed | 7228d4c7-a-project/01a098bc/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:80` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:106` | Overlap-ride-along | 7228d4c7-a-project/01a09790/review-3-findings.md |
| 336 | `src/Hall9k.Cli/Commands/ProjectRenameCommand.cs:59` | Overlap-ride-along | 7228d4c7-a-project/01a09790/review-3-findings.md |
| 336 | `src/Hall9k.Cli/Commands/TaskAssignCommand.cs:305` | Routed | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Daemon/AutoPrReview/AutoPrReviewEngine.cs:280` | Routed | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:405` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:651` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:752` | Genuine-miss | 7 findings file(s) checked, e.g. 7228d4c7-a-project/01a09790/review-1-findings.md |
| 336 | `src/Hall9k.Daemon/Dispatch/DispatchEngine.cs:1087` | Overlap-ride-along | 7228d4c7-a-project/01a09790/review-3-findings.md |
| 337 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:1836` | Overlap-ride-along | 5231e978-a-post-pr/01a0975f/review-1-findings.md |
| 337 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:947` | Genuine-miss | 9 findings file(s) checked, e.g. 5231e978-a-post-pr/01a0975f/review-1-findings.md |
| 337 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:1379` | Overlap-ride-along | 5231e978-a-post-pr/01a0975f/review-4-findings.md |
| 338 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:142` | Genuine-miss | 11 findings file(s) checked, e.g. 02ecf972-an-archiv/01a0982d/review-1-findings.md |
| 338 | `src/Hall9k.Cli/Commands/ProjectRemoveCommand.cs:255` | Genuine-miss | 11 findings file(s) checked, e.g. 02ecf972-an-archiv/01a0982d/review-1-findings.md |
| 338 | `src/Hall9k.Daemon/Purge/ProjectPurgeEngine.cs:70` | Overlap-ride-along | 02ecf972-an-archiv/01a09aab/review-1-findings.md |
| 338 | `src/Hall9k.Daemon/Purge/ProjectPurgeEngine.cs:115` | Overlap-fixed | 02ecf972-an-archiv/01a0982d/review-1-findings.md |
| 338 | `src/Hall9k.Daemon/Purge/ProjectPurgeEngine.cs:129` | Overlap-fixed | 02ecf972-an-archiv/01a099b1/review-1-findings.md |
| 338 | `src/Hall9k.Domain/Features/Project/Handlers/ProjectDecider.cs:424` | Overlap-ride-along | 02ecf972-an-archiv/01a0982d/review-1-findings.md |
| 338 | `src/Hall9k.Domain/Features/Tasks/Handlers/TaskDependencyResolver.cs:156` | Genuine-miss | 11 findings file(s) checked, e.g. 02ecf972-an-archiv/01a0982d/review-1-findings.md |
| 341 | `PLAN.md:1501` | No-local-record | no local task record (task may have run on a different node) |
| 341 | `src/Hall9k.Daemon/Closeout/RemoteBranchDeletionPolicy.cs:36` | No-local-record | no local task record (task may have run on a different node) |
| 341 | `src/Hall9k.Daemon/Closeout/RemoteBranchDeletionPolicy.cs:53` | False-positive | no local task record (task may have run on a different node) |
| 342 | `docs/operations.md:179` | Overlap-ride-along | d4414dcf-h9k-docto/01a09bb6/review-1-findings.md |
| 342 | `src/Hall9k.Cli/Diagnostics/ToolDoctor.cs:123` | False-positive | d4414dcf-h9k-docto/01a09c48/review-1-findings.md |
| 342 | `src/Hall9k.Cli/Diagnostics/ToolDoctor.cs:204` | Genuine-miss | 7 findings file(s) checked, e.g. d4414dcf-h9k-docto/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Cli/Commands/RunKillCommand.cs:81` | Genuine-miss | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Cli/Commands/RunKillCommand.cs:109` | Routed | ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:560` | Routed | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Execution/RunSupervisor.cs:801` | Routed | ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Execution/RunSupervisor.cs:2094` | Routed | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Review/PrReviewEngine.cs:983` | Genuine-miss | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:6142` | Routed | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 343 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:6221` | Routed | 8 findings file(s) checked, e.g. ca4af714-a-run-can/01a09bb6/review-1-findings.md |
| 344 | `claude/skills/orchestrator-recipe-generator/SKILL.md:482` | Genuine-miss | 4 findings file(s) checked, e.g. 9c06b841-an-idea-f/01a09bb6/review-1-findings.md |
| 344 | `docs/concepts.md:94` | Genuine-miss | 4 findings file(s) checked, e.g. 9c06b841-an-idea-f/01a09bb6/review-1-findings.md |
| 344 | `src/Hall9k.Cli/Commands/IdeaConcludeCommand.cs:45` | Genuine-miss | 4 findings file(s) checked, e.g. 9c06b841-an-idea-f/01a09bb6/review-1-findings.md |
| 344 | `src/Hall9k.Cli/Commands/IdeaShowCommand.cs:64` | Genuine-miss | 4 findings file(s) checked, e.g. 9c06b841-an-idea-f/01a09bb6/review-1-findings.md |
| 344 | `src/Hall9k.Cli/Commands/TaskAddCommand.cs:548` | Genuine-miss | 4 findings file(s) checked, e.g. 9c06b841-an-idea-f/01a09bb6/review-1-findings.md |
| 346 | `PLAN.md:1509` | Overlap-ride-along | 307f922b-the-dispa/01a09e9e/review-1-findings.md |
| 346 | `src/Hall9k.Domain/Features/Tasks/TaskRank.cs:69` | Genuine-miss | 5 findings file(s) checked, e.g. 307f922b-the-dispa/01a09cc7/review-1-findings.md |
| 348 | `src/Hall9k.Connectors/Ledger/GitLedger.cs:73` | False-positive | 5b1f57cd-a-ledger-/01a09f4f/review-1-findings.md |
| 348 | `src/Hall9k.Connectors/Ledger/GitLedger.cs:356` | Genuine-miss | 4 findings file(s) checked, e.g. 5b1f57cd-a-ledger-/01a09eea/review-1-findings.md |
| 348 | `src/Hall9k.Connectors/Ledger/LedgerRefRegistry.cs:98` | Overlap-ride-along | 5b1f57cd-a-ledger-/01a09eea/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/OwnerShowCommand.cs:80` | Genuine-miss | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:97` | Routed | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:106` | Routed | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:124` | Overlap-fixed | 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:165` | Routed | 126107bc-identity-/01a0a08b/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:308` | Genuine-miss | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Cli/Commands/StatusCommand.cs:304` | Genuine-miss | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 366 | `src/Hall9k.Connectors/Identity/NodeKeyStore.cs:57` | Overlap-fixed | 126107bc-identity-/01a0a08b/review-1-findings.md |
| 366 | `src/Hall9k.Connectors/Identity/NodeKeyStore.cs:97` | Genuine-miss | 6 findings file(s) checked, e.g. 126107bc-identity-/01a09fd3/review-1-findings.md |
| 370 | `src/Hall9k.Domain/Infrastructure/Persistence/EventOriginStampingListener.cs:68` | Genuine-miss | 7 findings file(s) checked, e.g. bfe95f6b-every-eve/01a0a07e/review-1-findings.md |
| 370 | `src/Hall9k.Domain/Infrastructure/Persistence/EventScopeRegistry.cs:84` | Overlap-ride-along | bfe95f6b-every-eve/01a0a07e/review-4-findings.md |
| 370 | `src/Hall9k.Domain/Infrastructure/Persistence/EventScopeRegistry.cs:173` | False-positive | bfe95f6b-every-eve/01a0a07e/review-4-findings.md |
| 370 | `src/Hall9k.Domain/Infrastructure/Persistence/MartenConfiguration.cs:46` | Overlap-fixed | bfe95f6b-every-eve/01a0a07e/review-1-findings.md |
| 374 | `claude/skills/orchestrator-recipe-generator/SKILL.md:306` | False-positive | 8308a52a-the-orche/01a0a23d/review-6-findings.md |
| 374 | `claude/skills/orchestrator-recipe-generator/SKILL.md:337` | Overlap-ride-along | 8308a52a-the-orche/01a0a17b/review-1-findings.md |
| 374 | `claude/skills/orchestrator-recipe-generator/SKILL.md:367` | Overlap-fixed | 8308a52a-the-orche/01a0a23d/review-1-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:122` | Overlap-ride-along | fdcc2a61-message-s/01a0a204/review-3-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:146` | Overlap-fixed | fdcc2a61-message-s/01a0a204/review-1-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/MessageInbox.cs:96` | Genuine-miss | 6 findings file(s) checked, e.g. fdcc2a61-message-s/01a0a146/review-1-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/MessageInbox.cs:106` | Routed | fdcc2a61-message-s/01a0a146/review-1-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/MessageInbox.cs:129` | Genuine-miss | 6 findings file(s) checked, e.g. fdcc2a61-message-s/01a0a146/review-1-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/MessageOutbox.cs:60` | Routed | fdcc2a61-message-s/01a0a204/review-3-findings.md |
| 375 | `src/Hall9k.Connectors/Messaging/MessageOutbox.cs:60` | Routed | fdcc2a61-message-s/01a0a204/review-3-findings.md |
| 375 | `src/Hall9k.Domain/Features/Message/MessageEnvelopeCodec.cs:86` | Genuine-miss | 6 findings file(s) checked, e.g. fdcc2a61-message-s/01a0a146/review-1-findings.md |
| 376 | `src/Hall9k.Connectors/Prompts/WorkPromptBuilder.cs:928` | False-positive | no local task record (task may have run on a different node) |
| 376 | `src/Hall9k.Daemon/Execution/RunLauncher.cs:490` | No-local-record | no local task record (task may have run on a different node) |
| 376 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2446` | No-local-record | no local task record (task may have run on a different node) |
| 376 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:4178` | No-local-record | no local task record (task may have run on a different node) |
| 379 | `src/Hall9k.Cli/Commands/MessageHandleCommand.cs:54` | Genuine-miss | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:82` | Genuine-miss | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:118` | Overlap-ride-along | 62acc347-message-d/01a0a324/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:161` | Genuine-miss | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:249` | Routed | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:494` | Genuine-miss | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/MessageOutbox.cs:44` | Routed | 62acc347-message-d/01a0a324/review-1-findings.md |
| 379 | `src/Hall9k.Connectors/Messaging/MessageOutbox.cs:171` | Routed | 62acc347-message-d/01a0a324/review-1-findings.md |
| 379 | `src/Hall9k.Daemon/Messaging/MessageSweepEngine.cs:164` | Overlap-fixed | 62acc347-message-d/01a0a324/review-1-findings.md |
| 379 | `src/Hall9k.Domain/Infrastructure/Persistence/EventOriginStampingListener.cs:65` | False-positive | 8 findings file(s) checked, e.g. 62acc347-message-d/01a0a1e5/review-1-findings.md |
| 380 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:3257` | Overlap-ride-along | 6189c968-the-stack/01a0a488/review-1-findings.md |
| 380 | `src/Hall9k.Daemon/Closeout/StackedParentWatch.cs:747` | Overlap-ride-along | 6189c968-the-stack/01a0a3a8/review-1-findings.md |
| 382 | `src/Hall9k.Connectors/WorkItems/ProjectGitHubAccessMirror.cs:89` | False-positive | 7 findings file(s) checked, e.g. 450b9d84-github-ad/01a0a07e/review-1-findings.md |
| 382 | `src/Hall9k.Connectors/WorkItems/ProjectGitHubAccessMirror.cs:157` | Genuine-miss | 7 findings file(s) checked, e.g. 450b9d84-github-ad/01a0a07e/review-1-findings.md |
| 382 | `src/Hall9k.Daemon/NodeContext.cs:29` | False-positive | 450b9d84-github-ad/01a0a07e/review-1-findings.md |
| 382 | `src/Hall9k.Domain/Features/Project/Handlers/ProjectDecider.cs:531` | Genuine-miss | 7 findings file(s) checked, e.g. 450b9d84-github-ad/01a0a07e/review-1-findings.md |
| 382 | `src/Hall9k.Domain/Infrastructure/Bootstrap/NodeBootstrap.cs:109` | Genuine-miss | 7 findings file(s) checked, e.g. 450b9d84-github-ad/01a0a07e/review-1-findings.md |
| 388 | `src/Hall9k.Daemon/Closeout/StackedParentWatch.cs:572` | False-positive | 6 findings file(s) checked, e.g. d28763ca-the-stack/01a0a4bd/review-1-findings.md |
| 388 | `src/Hall9k.Daemon/Execution/PullRequestOpener.cs:141` | Genuine-miss | 6 findings file(s) checked, e.g. d28763ca-the-stack/01a0a4bd/review-1-findings.md |
| 388 | `tests/Hall9k.Tests/Integration/StackedChildTests.cs:710` | False-positive | 6 findings file(s) checked, e.g. d28763ca-the-stack/01a0a4bd/review-1-findings.md |
| 392 | `src/Hall9k.Daemon/Closeout/CloseoutEngine.cs:973` | Overlap-ride-along | ccbab81e-the-force/01a0a652/review-1-findings.md |
| 392 | `src/Hall9k.Daemon/Execution/PullRequestOpener.cs:740` | False-positive | 6 findings file(s) checked, e.g. ccbab81e-the-force/01a0a50c/review-1-findings.md |
| 394 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2658` | False-positive | fb02f677-a-composi/01a0a551/review-1-findings.md |
| 394 | `src/Hall9k.Daemon/Review/ReviewEngine.cs:2663` | Overlap-ride-along | fb02f677-a-composi/01a0a551/review-1-findings.md |
| 394 | `tests/Hall9k.Tests/Integration/ReviewEngineTests.cs:3560` | Routed | fb02f677-a-composi/01a0a5fe/review-3-findings.md |
| 396 | `src/Hall9k.Daemon/Execution/StackReplayOntoResolver.cs:57` | False-positive | e5b2bea4-a-branch-/01a0a5ff/review-1-findings.md |
| 396 | `src/Hall9k.Daemon/Review/DecisionsLogRenumberer.cs:187` | Overlap-ride-along | e5b2bea4-a-branch-/01a0a5ff/review-3-findings.md |
| 396 | `src/Hall9k.Daemon/Review/DecisionsLogRenumberer.cs:358` | Genuine-miss | 6 findings file(s) checked, e.g. e5b2bea4-a-branch-/01a0a5ff/review-1-findings.md |
| 397 | `src/Hall9k.Cli/Commands/PullRequestReplyCommand.cs:135` | No-local-record | no local task record (task may have run on a different node) |
| 397 | `src/Hall9k.Cli/Commands/TaskShowCommand.cs:1293` | No-local-record | no local task record (task may have run on a different node) |
| 397 | `src/Hall9k.Connectors/Prompts/ReviewThreadReplyRoutes.cs:118` | No-local-record | no local task record (task may have run on a different node) |
| 397 | `src/Hall9k.Daemon/Closeout/GitHubPullRequestInspector.cs:417` | No-local-record | no local task record (task may have run on a different node) |
| 398 | `src/Hall9k.Cli/Commands/NodeRevokeCommand.cs:70` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/NodeRevokeCommand.cs:102` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/NodeVouchCommand.cs:75` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/NodeVouchCommand.cs:114` | Outcome-unrecoverable | eec9096d-trust-fil/01a0a7eb/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/NodeVouchCommand.cs:173` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/ProjectJoinCommand.cs:448` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/ProjectMemberRemoveCommand.cs:54` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Cli/Commands/StatusCommand.cs:394` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Messaging/GitLedgerMessageTransport.cs:257` | Outcome-unrecoverable | eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Trust/GitLedgerChainReader.cs:252` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Trust/GitLedgerChainReader.cs:392` | Outcome-unrecoverable | eec9096d-trust-fil/01a0a7eb/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Trust/GitLedgerChainReader.cs:466` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Trust/GitLedgerChainReader.cs:480` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Connectors/Trust/GitLedgerChainReader.cs:643` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Daemon/Messaging/MessageSweepEngine.cs:153` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 398 | `src/Hall9k.Daemon/Messaging/MessageSweepEngine.cs:197` | Outcome-unrecoverable | 8 findings file(s) checked, e.g. eec9096d-trust-fil/01a0a322/review-1-findings.md |
| 399 | `src/Hall9k.Cli/Commands/TaskLinkIssueCommand.cs:70` | Genuine-miss | 6 findings file(s) checked, e.g. c04000aa-every-exi/01a0a65d/review-1-findings.md |
| 399 | `src/Hall9k.Daemon/Execution/PullRequestOpener.cs:566` | False-positive | c04000aa-every-exi/01a0a765/review-1-findings.md |
| 399 | `tests/Hall9k.Tests/Integration/PullRequestOpenerTests.cs:143` | False-positive | 6 findings file(s) checked, e.g. c04000aa-every-exi/01a0a65d/review-1-findings.md |
