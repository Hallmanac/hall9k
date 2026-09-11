===step-numbered===
  4. Compose this pull request's title and description now, from the commits you just
===step-bulleted===
- **Compose this pull request's title and description before you finish**, from the commits you just
===made-line===
{{Indent}}made. This step writes no file, makes no commit and changes nothing in the
===worktree-numbered===
{{Indent}}worktree, so it cannot disturb the tree identity step 3 just verified.
===worktree-bulleted===
{{Indent}}worktree, so nothing about it touches the history you are leaving behind.
===whose-voice===
{{Indent}}- **Whose voice.** Follow the target repository's own PR-description rule when it
{{Indent}}  ships one — `.claude/commands/git/pr-description.md`, or a PR-description or
{{Indent}}  `pr-summary` skill under this worktree's own `.claude/skills/`. That rule wins for
{{Indent}}  the prose. Only when the repository ships none, follow the `pr-summary` skill
===installs-at-home===
{{Indent}}  Hall9k installs at `{{SkillPath}}`.
===installs-at-default===
{{Indent}}  Hall9k installs into this project's own skills directory.
===where-it-goes===
{{Indent}}- **Where it goes.** Into your final message, under a line reading exactly
{{Indent}}  `{{PrSummaryMarker}}`, placed before the `{{HandoffMarker}}` line: first line
{{Indent}}  `{{PrSummaryTitlePrefix}} <one line>`, then a blank line, then the body.
===what-to-leave-out===
{{Indent}}- **What to leave out.** The work-item link, the acceptance criteria, and the run
{{Indent}}  footer. The platform puts all three around your text, so a copy of any of them
{{Indent}}  in your own body is a second one a reviewer reads as a mistake.
===writing-conventions-lead-in===
**How it reads.** This project's writing conventions govern every word of the title and the body, which reviewers read on GitHub under the owner's login:
===do-not-run-gh===
{{Indent}}- Do not run `gh pr create` or `gh pr edit`: the platform opens the pull request,
{{Indent}}  and agents never do (PLAN.md §6.6).
