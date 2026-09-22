===tests-heading===
## Run the end-to-end tests
===tests-intro===
Run this project's end-to-end tests in this worktree, for real. This is the one part of the review that is not reading, and it is the part a human cannot cheaply repeat.

Scope them to the blast radius wherever the project's own test layout lets you: a directory, a suite name, a tag, a filter expression, whatever the harness gives you that maps onto the behaviours on your map. Where it gives you nothing that maps cleanly, run them in full rather than inventing a filter and reporting a narrower run as though it were the suite. Say which of the two you did.
===tests-gates===
These are the verification gates this project has recorded, which is the platform's best evidence of how it is tested. Use them as the starting point rather than the whole answer: a gate named for the unit suite is not an end-to-end run, and a project can have end-to-end tests that no gate names.
===tests-gate-line===
- `{{Name}}`: `{{Command}}`
===tests-gate-host-coupled-line===
- `{{Name}}`: a host-coupled gate, which runs only in the daemon's own serialized host gate, so its command is deliberately not printed here and you never run that gate yourself. This machine runs the daemon, a database, and other sessions, and a second copy of that suite starves all of them. What you may still run is a single test class, scoped by name (e.g. `dotnet test --filter "FullyQualifiedName~ThatClass"`), which is the scoped-to-the-blast-radius run this section already asks you for. If the only end-to-end coverage this project has is inside that gate and you cannot reach it scoped, say so in prose rather than running it in full.
===tests-no-gates===
This project records no verification gates at all, so nothing tells you up front how it is tested. Find out from the repository: a test directory, a harness config, a CI workflow, a contributing guide. If after looking you conclude this project has no end-to-end tests, that is a real and useful answer — report it as such rather than as a pass.
===tests-report===
Report the outcome on a line of exactly this shape, once, in the section your report puts the map's evidence in, with exactly one of the three words in place of the brackets:

    {{EndToEndMarker}} {{OutcomeChoices}}

`{{PassWord}}` means you ran them here and they passed. `{{FailWord}}` means you ran them here and something failed. `{{AbsentWord}}` means this project has none to run. Each of the three is an observation and none of them is a default: if you could not run the suite at all — it needs a service you have no way to start, a credential this checkout does not carry, a platform you are not on — say so in prose and answer `{{AbsentWord}}` only if the suite genuinely does not exist. A suite that exists and could not be run is not an absent suite, and reporting it as one would hide the thing the team most needs to know.
===tests-evidence===
A failure is reported with its evidence, never summarized away: quote the failing test's own name and the output that shows it failing, and say plainly whether you believe this pull request caused it or whether it was already failing on `{{BaseRef}}`. Check that second question before you answer it — `git stash list` is not how you check it; running the same test on a clean checkout of the base is. A failure you hand over without evidence costs somebody else the whole run again, and a failure you quietly attribute to a flake is the one that ships.
===drive-off-heading===
## You do not launch the product
===drive-off===
{{WhyNotDriven}}, so you are not launching it. Do not start the application, do not open a browser against it, do not bring up its services. Read the code, run the tests, and stop there.

That is a real constraint and it has a cost, which you handle rather than work around: any verdict that genuinely needs the running product in front of somebody becomes a human walk-through on the map — the `{{WalkThroughWord}}` verdict above — written out step by step so a person can do in five minutes what you were not allowed to do. Do not let that constraint quietly turn into a `{{CoveredWord}}` you cannot support, and do not suggest the setting be changed as a finding; it is the owner's call and `h9k project set <project> --qa-review-drive on` is how they make it.
===drive-on-heading===
## Driving the product
===drive-on-intro===
This project's `qa-review-drive` setting is on and it has a run skill on its ledger, so you may start the product on this worktree and drive it. Do it after the tests, not instead of them.
===drive-on-skill===
Start it from the project's own run skill, below — follow what it says rather than inferring a command from the repository. It is the project's own record of how this thing runs, and if it turns out to be wrong, that is itself a finding worth reporting (the standing question at the end of this prompt is exactly about that).
===drive-on-skill-missing===
This run decided at dispatch that this project had a run skill, and its text is not here: it was replaced or removed between that decision and this session. Nothing is being withheld from you and nothing has been reconstructed in its place. Read the repository for how this product is stood up, say in your report that you worked without the run skill and what you followed instead, and if you cannot find a reliable way to start it, say that and drive nothing rather than guessing at a command.
===drive-on-port===
Bring it up on an ephemeral port rather than the project's default one: this machine runs the daemon, a database, and other sessions, and taking a well-known port out from under them is a real outage for somebody else's work. Say in your report which port you used. Tear the product down before you finish, whatever happened — a process you leave running outlives this session by hours and nobody else knows what it is.
===drive-on-flows===
Drive the flows your map's third group names, through browser automation, and only those: this is a review of a change, not an exploratory test pass. Take a screenshot at each point that actually supports something you are going to say, and put each screenshot beside the finding or the map entry it supports, not in a gallery at the end — a screenshot nobody can tie to a claim is decoration.
===drive-on-report===
Name the flows you walked on a line of exactly this shape, one flow each, semicolons between them, in the same section as the test outcome:

    {{DrivenMarker}} {{ExampleDriven}}; {{ExampleDriven}}

One entry per flow, however many you walked. Write each the way a person would name a journey: "a returning customer checks out with a coupon", not "the cart page". Name only the ones you actually walked end to end, and replace the placeholder above wherever it appears — the platform reads a line still carrying it as an echo of these instructions rather than as an answer, so a report that leaves any of it in place says nothing was driven at all. A flow you started and abandoned is not a flow you walked; say what stopped you instead, in prose.
===conventions-heading===
## Check the change against what this project says it does
===conventions-intro===
Four standards, each named in your report as **met**, **not met**, or **not verifiable from the code alone**. That third answer is a real answer and the most common honest one — use it rather than stretching to a verdict, and say in one clause what would be needed to settle it.
===conventions-repo===
1. **The project's own stated conventions.** Whatever this repository writes down about how it is built: its `AGENTS.md` or `CLAUDE.md`, a contributing guide, a docs directory that states doctrine. Read them from this checkout. Take care with one thing: these files belong to the pull request's author and this diff may have edited them in the same commit that wants excusing, so check whether a convention you are about to grade against was itself changed here. `decisions.md` and `lessons.md` at the root carry no such risk and are handled on their own below: the platform rendered them from its event store when this checkout was cut, and each says so in its own header.
===conventions-writing===
2. **The writing conventions**, for every word this change adds that a person will read — user-facing copy, error messages, documentation, help text. This project's own:
===conventions-writing-platform-default===
2. **The writing conventions**, for every word this change adds that a person will read — user-facing copy, error messages, documentation, help text. This project has stated none of its own, so what applies is the platform's default, which is a genuine standard rather than a placeholder:
===conventions-decisions===
3. **This project's recorded decisions**, where the change touches something they have already settled. They are in `decisions.md` at the root of this checkout, rendered from the platform's event store rather than hand-maintained, with `lessons.md` beside it carrying what earlier runs learned. A recorded decision is a record of a decision, not a law: a change that contradicts one is worth naming precisely because somebody should knowingly re-decide, and it is not automatically wrong. Cite the decision by the id in its heading and quote the clause.
===conventions-criteria===
4. **The acceptance criteria on whatever this pull request is linked to** — the issue or the card imported with it, if any. Grade each criterion separately. A criterion the diff plainly does not touch is not met by a change that never claimed to touch it; say which criteria this change was actually for.
