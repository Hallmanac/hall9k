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
{{Indent}}  ships one: `.claude/commands/git/pr-description.md`, or a PR-description or
{{Indent}}  `pr-summary` skill under this worktree's own `.claude/skills/`. That rule wins for
{{Indent}}  the structure, the shape and the title convention; the owner's own voice below
{{Indent}}  wins for the prose when you find one, and the rule's own plain colleague voice
{{Indent}}  when you do not. Only when the repository ships none, follow the
{{Indent}}  `pr-summary` skill
===whose-voice-voiced===
{{Indent}}- **Whose voice.** Follow the target repository's own PR-description rule when it
{{Indent}}  ships one: `.claude/commands/git/pr-description.md`, or a PR-description or
{{Indent}}  `pr-summary` skill under this worktree's own `.claude/skills/`. That rule wins for
{{Indent}}  the structure, the shape and the title convention; the owner's own voice below
{{Indent}}  wins for the prose. Only when the repository ships none, follow the
{{Indent}}  `pr-summary` skill
===installs-at-home===
{{Indent}}  Hall9k installs at `{{SkillPath}}`.
===installs-at-default===
{{Indent}}  Hall9k installs into this project's own skills directory.
===no-voice-skill-named===
{{Indent}}- **The owner has named no voice skill, so look for one by name.** Check for a
{{Indent}}  skill called `my-voice`, first in the owner's own user skills directory
{{Indent}}  (`~/.claude/skills/my-voice`, which this session can already see) and then in
{{Indent}}  this worktree's own `.claude/skills/my-voice`. When one is there, load it with
{{Indent}}  its `{{VoiceContext}}` context, or whichever context it carries for a
{{Indent}}  pull request description, before you write a word of this, and write the way
{{Indent}}  they write: plain language, high-level concepts, complete sentences, the way
{{Indent}}  they would explain this change to a colleague. The repository's own
{{Indent}}  PR-description rule and this project's writing conventions still decide the
{{Indent}}  structure; the voice skill decides only the prose. When neither tier has one,
{{Indent}}  say so in one line of your final summary and write the body in the plain
{{Indent}}  colleague voice the rule itself describes.
===how-long===
{{Indent}}- **How long it runs.** The body is proportionate to the diff, not to the effort
{{Indent}}  behind it. A one-file change, or a documentation-only one, gets one or two
{{Indent}}  sentences of orientation plus only the judgment calls and the reviewer actions
{{Indent}}  that actually exist, which is often neither: three or four lines in total. A
{{Indent}}  change spread across many files earns the grouped bullets and the paragraphs
{{Indent}}  the rule describes. Nothing here is a section you fill in because it exists.
===the-title===
{{Indent}}- **The title.** One line saying what the change does rather than what you did to
{{Indent}}  the code. When this task carries an external key, write the key on the front in
{{Indent}}  the same form this repository's own commit subjects carry one, which a glance
{{Indent}}  at `git log` on the base branch answers: with a colon after the key when they
{{Indent}}  use one, and without when they do not. The platform adds a key only to a title
{{Indent}}  that carries none at all, so writing it yourself is what keeps the whole line
{{Indent}}  in your own wording.
===where-it-goes===
{{Indent}}- **Where it goes.** Into your final message, under a line reading exactly
{{Indent}}  `{{PrSummaryMarker}}`, placed before the `{{HandoffMarker}}` line: first line
{{Indent}}  `{{PrSummaryTitlePrefix}} <one line>`, then a blank line, then the body.
===what-to-leave-out===
{{Indent}}- **What to leave out.** The work-item link, which the platform writes above your
{{Indent}}  prose and is the only thing it adds. A restatement of what the diff contains,
{{Indent}}  which a reviewer is already looking at. Build and test attestations, which
{{Indent}}  belong in the run record and in CI. Anything you are already writing under
{{Indent}}  `{{HandoffMarker}}`, which is for the next session rather than for a reviewer.
{{Indent}}  Any restatement of this task's acceptance criteria, which live on the card.
===writing-conventions-lead-in===
**How it reads.** This project's writing conventions govern every word of the title and the body, which reviewers read on GitHub under the owner's login:
===do-not-run-gh===
{{Indent}}- Do not run `gh pr create` or `gh pr edit`: the platform opens the pull request,
{{Indent}}  and agents never do (PLAN.md §6.6).
