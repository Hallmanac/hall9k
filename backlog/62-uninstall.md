---
project: hall9k
type: feature
objective: h9k uninstall takes the platform off a machine without taking the work with it - binaries, links, autostart, and the install home go; the database and its data survive by default, and a reinstall finds them again
criteria:
- h9k uninstall stops a running daemon, unregisters autostart where enabled (launchd agent or Windows logon task), removes the PATH link, and removes ~/.hall9k including bin/ and the config file - a removed home is a removed home, per the walk ruling
- The Postgres container is stopped but never removed, and its data volume is never touched: data lives in Docker, not in the install home, and uninstall's summary says exactly that so the operator knows their work is safe
- h9k uninstall --purge-data is the only path that destroys the container and volume; it names what is about to die and asks for consent before anything happens, refusing in a non-interactive session without an explicit yes flag
- A reinstall on a machine with a surviving container reconnects to it: the existing doctor detect-and-start flow is verified against the post-uninstall state, and a full uninstall then reinstall cycle ends with every task, run, and idea readable exactly as before
- The command teaches: help text and the final summary both distinguish the two tiers, and INSTALL.md gains the uninstall section
- dotnet build and dotnet test pass
---
Origin (2026-08-24, the Windows walk): Brian's install-testing loop needs to
run the stranger-installs-from-the-README scenario repeatedly on one real
Windows machine, which requires tearing the install down between runs - but
by the second run he may have real work in the database, so tearing down must
not mean losing it. The design fell out of the walk: the home directory only
ever holds what the install wrote (binaries, config, compose file), while the
data lives in Docker's own container and volume, so default uninstall removes
the former and strands nothing. The caveat recorded for honest testing: a
default uninstall-reinstall is not a first-install test, because the data
survives; --purge-data is the true stranger path.

Relationship: install family, beside 59 (durable config) and 61 (no dev
settings); 60 (Windows lifecycle) leans on this for its repeated proof runs.
Disjoint from 59's config-binder footprint, so the two can run in parallel.
