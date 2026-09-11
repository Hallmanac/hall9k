===heading===
## Where you are
===with-worktree===
A read-only checkout of this pull request's head is at `{{WorktreePath}}` — a detached worktree of project {{ProjectName}}'s clone at `{{RepositoryPath}}`, with no local branch, so there is nothing here that could be pushed by accident. The base branch is available as `origin/{{BaseRef}}`, so `git diff origin/{{BaseRef}}...HEAD` is the pull request's own range.
===without-worktree===
No checkout was made for this lap (`--no-worktree`): the reviewer is testing against a deployed environment rather than reading the code locally. If they later want the code in front of them, say so — the lap can be re-run without that flag.
