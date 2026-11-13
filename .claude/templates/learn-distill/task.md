===objective===
Distil {{Scope}}'s {{LessonCount}} active lessons into fewer, better ones, merging and citing only
===criterion-merge-only===
Every lesson you record here is a merge of lessons already on record. Record it with `h9k learn "<the merged claim>" --distilled-from <id> --distilled-from <id> --task {{TaskId}}`, citing each lesson it merged. The platform refuses a citation that resolves to nothing, but a plain `h9k learn` with no `--distilled-from` is recorded without refusal, so citing every source is your own discipline here.
===criterion-no-new-claims===
A merged lesson says nothing its sources do not say: every claim in it appears in at least one lesson it cites. If reading the inventory gives you a genuinely new insight, do not record it; name it in the findings document for a person to decide.
===criterion-retire-sources===
Each lesson merged away is retired with `h9k learn retire <id> --reason "Absorbed into <the surviving id>"`. A lesson whose whole claim another live lesson already states may be retired into that existing lesson the same way, with no new lesson recorded.
===criterion-retire-evidence===
A lesson may be retired as wrong or obsolete only when its reason names the evidence at the checkout's commit that shows it no longer holds: a file and line, or the absence of a named file or symbol. A lesson without that evidence is left alone.
===criterion-leave-alone===
A lesson that says one thing nothing else says is left exactly as it is.
===criterion-held-out===
A lesson is held out exactly when the "In prompts" line of `h9k learn show <id>` says no: it is read-only for this task, cited in no merge, and named in the findings document under "held out". A lesson recorded on another node of the owner's own fleet whose "In prompts" line says yes is an ordinary lesson for this task.
===criterion-merge-length===
A recorded merge becomes the newest lesson, so it is the first one in the injected prompt section, and that section ends at the first lesson that does not fit its character budget, displacing recent lessons. Do not record a merge that is longer than its sources together.
===criterion-account===
The inventory is the active list read at session start with `h9k learn list {{ListArguments}} --limit 500`. The findings document's "What was observed" section names every lesson in it by id, under exactly one of these headings: merged into <new id>; absorbed into existing <id>; retired as wrong or obsolete, with its reason; left alone; held out; not reached.
===exit-criterion===
The findings document's "What was observed" section names by id every lesson in the active list read at session start with `h9k learn list {{ListArguments}} --limit 500`, each under exactly one of these headings: merged into <new id>; absorbed into existing <id>; retired as wrong or obsolete, with its reason; left alone; held out; not reached. It gives that account even when no merge was warranted. Every lesson this run recorded carries `--distilled-from` and `--task {{TaskId}}` and cites no held-out lesson, and every lesson merged or absorbed away is retired citing its survivor.
===context===
What kind of task this is. This is a research spike, not a build task: it ends with a findings
document and no build or test gate, and the platform opens no pull request for it. Recording and
retiring lessons with `h9k learn` is this spike's work and is not the code a research spike is told
not to write, so do both freely. Name this task on every lesson you record by adding
`--task {{TaskId}}` to the `h9k learn` command: it is what credits the lesson to this run, and
without it the lesson lands marked as recorded with no run named, which reads to the next session
as though a person might have typed it. Put the account of the inventory inside the findings document's
"What was observed" section: every lesson in the inventory, the active list read at session start
with `h9k learn list {{ListArguments}} --limit 500`, appears there by id under exactly one of these
headings: merged into <new id>; absorbed into existing <id>; retired as wrong or obsolete, with its
reason; left alone; held out; not reached. Give that account even when no merge was warranted.

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
the two. A lesson is held out exactly when the "In prompts" line of `h9k learn show <id>` says no.
Those are the lessons recorded on a node outside the owner's fleet, replicated from a node outside
it (each also when the fleet cannot be read), recorded on a node nobody recorded, or recorded with
no provenance; they are rendered in `lessons.md` and kept out of prompts pending the security
review in idea 7e403b80, so merging one INTO a lesson that does reach prompts would route its claim
around that hold. Treat a held-out lesson as read-only for this task: cite it in no merge, and name
it in the findings under "held out". A lesson recorded on another node of the owner's own fleet
reaches prompts, and when its "In prompts" line says yes it is an ordinary lesson for this task.

The live inventory is `h9k learn list {{ListArguments}} --limit 500`, which is what you should
actually work from. The snapshot below was taken when this task was authored, so treat it as
orientation rather than as the set: lessons can be recorded and retired between then and now.
