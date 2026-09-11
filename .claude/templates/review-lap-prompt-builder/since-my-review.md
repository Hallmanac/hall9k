===heading===
## What has changed since your review
===scope-intro===
This is a **scoped lap**. The reviewer ({{ReviewerLogin}}) has already reviewed this pull request once, and this briefing is only what has arrived since: replies on the threads they opened, and the commits pushed after their review. The objective, the blast radius, the CI results and the platform's own earlier findings report are deliberately NOT here — they were read in the first lap and re-reading them is what this flag exists to avoid. Do not reason as though the packet below were the whole pull request; when something in it needs wider context, go and read that context in the checkout rather than assuming it away.
===head-known===
Their review was posted against `{{ReviewedShort}}`; the head is now `{{CurrentShort}}`.
===head-unknown===
The platform has no record of which commit their review was posted against, so the code half of this packet is the commits it could observe rather than a range pinned to their review. Say so if it matters to a finding.
===re-request-notice===
**The author has re-requested this review**, which is an explicit ask to look again whatever the packet below turns out to hold — a re-request with no reply and no push is still an ask.
===thread-replies-heading===
### Thread replies
===page-truncated===
**This pull request carries more review threads than the provider's own page cap can return (100), so the thread half of this packet is incomplete.** Every count below is a floor, and threads the reviewer opened may be missing from it entirely — an absent thread here does NOT mean it went quiet. Read them on GitHub before treating any silence below as an answer: `gh pr view {{RepoAndNumber}} --comments`.
===none-moved-opening===
None of the reviewer's own threads have moved since their review
===none-moved-unchanged-truncated===
 — all {{Count}} of theirs that could be read are unchanged.
===none-moved-unchanged-not-truncated===
 (all {{Count}} of them are unchanged).
===none-moved-none-truncated===
 — none of theirs were inside the page that could be read.
===none-moved-none-not-truncated===
 — they opened none.
===none-moved-rerequest-tail===
 What prompted this lap may be nothing more than the re-request above; say so plainly if the code half below is empty as well.
===none-moved-no-rerequest-tail===
 Whatever prompted this lap is in the code half below.
===moved-summary-opening===
{{MovedCount}} of the reviewer's threads moved
===moved-summary-with-unchanged===
; {{UnchangedCount}} more are unchanged and are not shown.
===moved-summary-no-unchanged===
.
===moved-summary-tail===
 Every reply is verbatim.
===no-new-comment===
(No new comment; the thread's own state is what changed.)
===unread-comment-notice===
(**{{UnreadCount}} further comment(s) on this thread are past the provider's own page cap and are NOT shown here** — and they are the most recent ones, so the last word in this thread is not above. Read the thread on GitHub before drawing a conclusion from it.)
===commits-heading===
### Commits pushed since your review
===commits-none===
None were observed.
===what-to-produce-heading===
### What to produce
===what-to-produce-body===
Read the packet above and report findings in the same shape the first lap's report used: one heading per finding, each naming the file and line it is about, what is wrong, and how confident you are. A reply that answers the original finding correctly is itself a finding worth stating — "this one is addressed" is what lets the reviewer resolve the thread. Nothing you write is posted anywhere; the reviewer directs each finding themselves, exactly as they did the first time.
