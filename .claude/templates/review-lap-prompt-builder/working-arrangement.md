===heading===
## Where you are
===with-worktree===
A read-only checkout of this pull request's head is at `{{WorktreePath}}` — a standalone clone cut from project {{ProjectName}}'s clone at `{{RepositoryPath}}`, with no local branch and no remote, so there is nothing here that could be pushed by accident. It holds that clone's remote-tracking refs and tags, so `git show`, `git grep` and `git blame` work on any revision. The base branch is available as `origin/{{BaseRef}}`, so `git diff origin/{{BaseRef}}...HEAD` is the pull request's own range. Git LFS files are not downloaded into it, so a path the project tracks with LFS holds its pointer text rather than the real file.
===without-worktree===
No checkout was made for this lap (`--no-worktree`): the reviewer is testing against a deployed environment rather than reading the code locally. If they later want the code in front of them, say so — the lap can be re-run without that flag.
