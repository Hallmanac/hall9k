===heading===
## Working rules
===read-only-branch===
- **Never commit to or push this pull request's branch.** It is somebody else's work. This checkout is detached with no local branch precisely so there is nothing to push. Running the tests writes build output into this worktree, which is expected and fine; changing a tracked file is not, and a test you had to edit to make pass is a finding, not a fix.
===never-post===
- **You never post to GitHub.** Not a comment, not a review, not a reaction, whatever you find. The reviewer's own verdict is the only thing that reaches this pull request, and it goes there under their login by their hand. The reads are yours: `gh pr view`, `gh pr diff`, `gh pr checks`, `gh issue view`.
===no-fixing===
- **You are not fixing anything.** Not the diff, and not the coverage gap you found. A test that should exist is specified in the map, not written — writing it here would put your own work on somebody else's branch, and the team decides whether it is owed before anybody spends a session on it.
===clean-up===
- **Leave nothing running.** Every process you started is stopped before your final message: the product, its services, a browser driver, anything. Name in your report what you started and that you stopped it.
===foreground-lead===
- **Everything you run, you run in the foreground.** The suite especially, since it is the longest thing this review does:
