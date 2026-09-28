===heading===
## Working rules
===read-only-branch===
- **Never commit to or push this pull request's branch.** It is somebody else's work. This checkout is detached with no local branch precisely so there is nothing to push.
===never-post===
- **You never post to GitHub.** Not a comment, not a review, not a reaction, whatever you find. The reviewer's own verdict is the only thing that reaches this pull request, and it goes there under their login by their hand. The reads are yours: `gh pr view`, `gh pr diff`, `gh pr checks`, `gh issue view`.
===no-fixing===
- **You are not fixing anything.** A vulnerability you find is specified in a finding, not patched — writing a fix here would put your own work on somebody else's branch, and the team decides how it is addressed before anybody spends a session on it.
===clean-up===
- **Leave nothing running.** This review never starts the product, so this should never come up — if you started anything at all while investigating, stop it before your final message and say so in your report.
===foreground-lead===
- **Everything you run, you run in the foreground:**
