# Idea: stacked pull requests for dependency chains

Captured 2026-08-20 from Brian: when a group of related tasks forms a dependency
chain, the platform should be able to deliver them as stacked PRs - each task's
branch cut from its parent task's branch instead of main, each PR based on its
parent's branch - so agents can keep producing while human reviewers catch up.

## Why (the team-scale motivation)

Today one human reviews everything, so tasks run one merge at a time and the
pipeline waits on the review checkpoint. On a team, colleagues review PRs on
human timescales while agents generate work on agent timescales; without
stacking, a dependency chain serializes on review latency (task B cannot even
branch until task A merges). With stacking, B branches from A's branch, opens
its PR with base A, and the chain keeps moving; reviewers work through the
stack in order.

## How it rides existing machinery

- **Backlog 05 is the trigger**: a BlockedBy edge is exactly the signal that B
  stacks on A. Today "blocked" means "wait for A's true closeout"; with
  stacking, a blocked-on-A task becomes dispatchable EARLY, branching from A's
  branch - stacking is an alternative unblock mode, chosen per chain or per
  project, never silently.
- **The worktree manager** already knows how to branch from an arbitrary ref;
  cutting from task/A instead of origin/main is a parameter, not a rewrite.
- **The closeout monitor** gains the retarget job: when A's PR merges, B's PR
  rebases onto main and its base flips from task/A to main (gh pr edit
  --base). When A's branch is force-pushed (narrative fixups), every child in
  the stack rebases - the monitor already observes both events.
- **The review loop** is unchanged: each stacked task still gets gates and an
  independent pre-PR review against its own diff (diff against parent branch,
  not main - the seam to get right).

## Hard parts to respect in design

- Cascade cost: a fixup deep in the stack rebases every descendant; bounded
  chains (3-4 deep) before the platform suggests merging the base.
- A parent's review rejection invalidates children built on it - the stack
  needs an honest story for "A changed underneath B" (requeue B's follow-up?
  park the chain?).
- CI runs per stacked PR show the chain's cumulative diff unless bases are set
  correctly; base management IS the feature.

## Dependencies and fit

After backlog 05 (the graph must exist), likely after the coordinator agent
idea matures (chains become common once edges are cheap to author). Single-node
single-reviewer mode keeps working unchanged - stacking is opt-in delivery for
team-scale review throughput.
