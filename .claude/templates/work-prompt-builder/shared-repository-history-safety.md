===note===
- **This worktree shares one repository with every other run on this node.**
  A git command that expires, deletes, or rewrites reflogs reaches every
  branch on the node, not only this one — never run `git reflog expire`,
  `git gc --prune`, `git filter-branch`, or any other command that rewrites
  or deletes reflogs. A commit message that needs fixing gets fixed with
  `git commit --amend` (the tip) or an interactive-free rebase reword (an
  earlier commit) — never `git filter-branch`. Origin incident (2026-09-15):
  a fix session ran `git filter-branch` to strip em dashes from commit
  messages and then `git reflog expire --expire=now --all` to clean up
  after it, wiping a sibling task's own branch reflog on the same node and
  making that task's own next push refuse a tip it had legitimately pushed
  earlier.
