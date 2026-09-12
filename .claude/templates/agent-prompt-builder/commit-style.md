===append-style===
- Commit your fixes on this branch with clear messages, on top of the existing
  history (this project uses the append commit style). Do NOT push, do NOT open
  a new pull request — the platform re-verifies and pushes after you finish; the
  existing PR updates in place.
===narrative-lead===
- Land your fixes as authored history (this project uses the narrative commit
  style): the PR branch must read as a natural progression of the whole change,
  so fold each fix into the commit that owns it instead of appending
  review-feedback commits. If the repo ships an absorb-review-fixes skill, invoke
  it — it walks these exact mechanics. Either way:
===stacked-skill-caveat===
  - That skill assumes a branch cut off the project's own base branch, and this one
    is not: wherever it names `origin/{{BaseBranch}}` as the fold's upstream, use the
    commit named below instead. The rest of it applies unchanged.
===map-and-land-fixups===
  - Map each fix to the most recent branch commit that touches the same file and
    land it with `git commit --fixup=<owning-commit>`. A fix spanning files owned
    by different commits splits into one fixup per owning commit. Genuinely new
    scope (a new file no commit owns) may be a new, properly-titled commit —
    never "review fixes" or "address feedback".
  - With every fix committed, record the pre-rebase tip (`git rev-parse HEAD`),
    then fold the fixups into their owning commits:
===fold-command===
    `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash {{Argument}}`.
===tree-identity-and-push===
  - REQUIRED before you finish: verify tree identity — `git diff <old-tip> HEAD`
    must print nothing. A non-empty diff means the rebase changed the content and
    the verification results no longer describe this tree; reconcile until the
    diff is empty. Only a tree identical to the tested one may be force-pushed.
  - Do NOT push (the platform pushes the rewritten branch with
    `git push --force-with-lease` after re-verifying), and do NOT open a new pull
    request — the existing PR updates in place.
