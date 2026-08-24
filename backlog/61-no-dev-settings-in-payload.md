---
project: hall9k
type: bugfix
objective: Development settings never ship - the publish and release payloads exclude appsettings.Development.json, so an installed machine carries only what production is meant to read
criteria:
- The published output for both h9k and h9kd excludes appsettings.Development.json (and any future *.Development.* sibling) via the project files, so every consumer - h9k install --repo, the release workflow, a bare dotnet publish - gets the exclusion for free rather than each remembering it
- The install and update staging paths are verified not to copy a Development settings file even when one exists in a stale source directory
- A release payload built by the workflow is inspected (in a test or in the workflow itself) and fails loudly if a Development settings file is present
- dotnet build and dotnet test pass
---
Origin (2026-08-24): Brian found appsettings.Development.json sitting in
~/.hall9k/bin beside appsettings.json - shipped by the local h9k install
publish, and the release workflow would ship it identically. Today's copy is
benign (logging levels only, and the installed daemon runs Production so the
file is not even loaded), which is exactly why this is filed now rather than
after it matters: the failure mode is a future developer putting a dev
connection string or a chatty flag in that file and having it ride into every
install. Rides in the delivery-hygiene family beside backlog 42's landed work.
