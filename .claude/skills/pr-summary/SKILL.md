---
name: pr-summary
description: Generate a pull-request title and description from the commits on the current branch. Use when a branch's work is finished and needs a PR body — the output is text only; it never opens the PR.
---

# PR Summary Generator

Create a pull-request title and description by reviewing the commits on the current branch. The output is the deliverable: **never run `gh pr create` or open a PR** — the Hall9k daemon opens PRs (`PullRequestOpener`); agents are forbidden from doing so.

## Process

1. **Gather commit information** (in parallel):
   - Commits on this branch vs the base: `git log --oneline origin/main..HEAD` (base is `main` unless told otherwise)
   - Full messages and bodies: `git log origin/main..HEAD`
   - Change shape: `git diff --stat origin/main..HEAD`

2. **Generate the PR title and description** from the commits and diff. If the "why" behind a change isn't clear from the commits or repo docs (PLAN.md, SLICE-1.md), don't invent one — note the gap under an "Open questions" line at the end of the description instead of asking.

## Output guidelines

**Audience**: technical developers familiar with the codebase and its patterns.

**Format**:
- **PR Title** — concise; include the slice task ID (e.g. `S1-10`) if the commits carry one; describes the main change
- **Summary** — high-level "what" changed in the system
- **Why** — non-obvious reasons only: architectural, performance, or maintainability motivations. Skip the obvious (nobody needs to be told why tests were added).
- **Key Changes** — bulleted list, organized logically
- **Technical Details** — only if there are implementation specifics reviewers will want

**Style**:
- Scannable first: a reviewer glancing over it gets the shape; slower reading reveals more.
- Keep test-coverage mentions brief ("Added unit tests for ServiceX", not line counts).
- Markdown formatting (headers, bullets, code blocks, bold).
- Output the final title and description in a fenced code block so it can be copied or consumed verbatim.
