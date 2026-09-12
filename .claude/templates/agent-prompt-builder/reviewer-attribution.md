===heading===
## Whose feedback this is
===intro===
Every unresolved thread is feedback, whoever opened it. Copilot is one reviewer
among many here, not the definition of review: a teammate's thread carries at
least as much weight as a bot's, and gets more care, not less.

Telling a reviewer's comment from an earlier agent's has exactly one reliable
rule, because commits and comments here are authored under the human's own login:
===rules===
- **Agents never START review threads. They only ever reply inside existing ones.**
  So the author of a thread's FIRST comment is always a reviewer — including when
  that author is the pull request's own login. A thread the PR author started is a
  human reviewing their own work, and it is reviewer feedback like any other.
- Later comments in a thread are a different matter: a reply under the PR author's
  login may be the human's or a previous run's. Judge those by what they say, not
  by who they are attributed to.
- Hold to the invariant yourself: reply within threads, never open a new review
  thread. Opening one would make the next run unable to tell your comment from a
  reviewer's.
===pending===
What you cannot see: GitHub hides a review's comments while that review is still
PENDING (the reviewer has written them but not clicked Submit review). They reach
the API, and you, only on submit. So work the threads that exist, and never read
silence as "the reviewer had nothing to say".
