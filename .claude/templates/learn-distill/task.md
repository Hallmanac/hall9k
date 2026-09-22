===objective===
Distil {{Scope}}'s {{LessonCount}} active lessons into fewer, better ones, merging and citing only
===criterion-merge-only===
Every lesson you record here is a merge of lessons already on record, and it cites each one it merged: h9k learn "<the merged claim>" --distilled-from <id> --distilled-from <id>. The decider refuses a distilled lesson that cites nothing, so there is no way to record a merge without the citations, and that is deliberate.
===criterion-no-new-claims===
You record nothing this project has not already learned. If reading the inventory gives you a genuinely new insight, that is not this task's output: say so in your final summary and let a human decide, rather than recording it here where it would arrive dressed as a merge of things that were actually observed.
===criterion-retire-sources===
Each lesson you merged away is retired explicitly, citing the survivor: h9k learn retire <id> --reason "Absorbed into <the new id>". Merging does not retire anything on its own, and a source left live means the same claim now rides in a prompt twice.
===criterion-leave-alone===
A lesson that says one thing nothing else says is left exactly as it is. The goal is fewer lessons that are each worth a prompt's line, not the smallest possible number: two claims forced into one sentence teach less than the two did.
===criterion-account===
Your final summary accounts for every lesson in the inventory below under one of three headings: merged into <new id>, retired as wrong or obsolete with its reason, or left alone. A lesson you did not reach is named as one rather than left unmentioned.
===context===
Why this task exists: every dispatched session on this project carries a bounded section of its
active lessons, and the section has two caps (a lesson count and a character budget). Past those
caps the oldest lessons stop reaching prompts at all. Retirement is the cheap control and it is
already available to every run; distillation is the other one, and it is deliberately a task a
human authors and publishes rather than something the daemon decides to do on its own judgment.
Nothing in this platform distils automatically, and nothing should: merging two claims into one is
a judgment about meaning, and a wrong merge is worse than two lessons that overlap.

What "merge and cite only" rules out, concretely. You are not here to improve the wording of a
lesson that stands on its own. You are not here to record what you think this project should have
learned. You are not here to generalise three specific observations into one abstract principle
that none of them actually established, which is the failure mode this instruction exists to
prevent: the abstraction reads better, survives every review, and is not something anybody
observed. If two lessons say the same thing in different words, merge them and cite both. If three
lessons are three faces of one rule that all three genuinely support, merge them and cite all
three. Otherwise leave them alone.

How to read a lesson before you touch it. `h9k learn show <id>` gives you its provenance: which
run and task recorded it, if any, which node it was recorded on, and whether it reaches prompts
at all. Read "recorded with no run named" as exactly that and no further: a person typing at a
shell records it, and so does an agent that skipped `--task`, and nothing on the record separates
the two. A lesson recorded by an agent on another node is rendered in `lessons.md` and held
out of prompts pending the security review in idea 7e403b80, so merging one INTO a lesson that
does reach prompts
would route its claim around that hold. Do not do that: treat a held-out lesson as read-only for
this task, name it in your summary, and leave it.

The live inventory is `h9k learn list {{ListArguments}}`, which is what you should actually work
from. The snapshot below was taken when this task was authored, so treat it as orientation rather
than as the set: lessons can be recorded and retired between then and now.
