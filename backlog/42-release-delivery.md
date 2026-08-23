---
project: hall9k
type: feature
objective: A tagged commit becomes installable binaries on a bare machine - release CI, a one-line bootstrap, and the existing install path fed by artefacts instead of a local build
criteria:
- A tagged commit produces a GitHub release carrying h9k and h9kd binaries for macOS arm64 and Windows x64, built by a release workflow alongside the existing ci.yml
- A one-line bootstrap (per platform) fetches the latest release for the current platform, places the binaries in ~/.hall9k/bin, and puts h9k on PATH - with no repo checkout and no dotnet SDK on the machine; gh authentication is the stated prerequisite for a private repo's releases. The script and artefacts are hosted entirely in GitHub (raw script + Releases), no other host
- The bootstrap verifies the downloaded artefact's checksum against the release before installing, asks consent before changing the machine, publishes the canonical skill set to ~/.hall9k/skills, and finishes by running h9k doctor (backlog 24) so a fresh machine ends setup knowing what still needs attention
- An agent-installable path exists: a hosted INSTALL doc (fetchable raw from the repo) that an AI agent can follow end to end, so "tell your session to install Hall9k" works on machine zero
- Installing registers no background service and no autostart, exactly as S1-12 shipped
- After bootstrap, the existing idempotent install/update path works against released artefacts rather than a local build, and says which version it placed
- h9k update is the one-command path for a machine that is already installed: it fetches the latest release for the platform, republishes idempotently, republishes the canonical skill set, and offers the daemon restart - the update mechanism a second machine needs to stay current without a repo checkout
- backlog/12-daemon-install.md is corrected: the Windows autostart deferral is stale - autostart enable registers a Windows startup task on Windows and a launchd LaunchAgent on macOS, already implemented
- dotnet build and dotnet test pass
---
Gap 1 and gap 2 of backlog/IDEA-installation-and-materialisation.md (Brian's
2026-08-22 design session; the sitting addendum records the store ruling and
resolutions). The platform matrix is a build-and-publish concern - the autostart
work already split platform behaviour in code.

Framing inspiration (Brian, 2026-08-23): Atlassian's TWG CLI installer - a
curl-to-bash script that checksums, asks consent, installs binaries AND agent
skills, then runs its doctor; twg update for staying current; and an
agent-assisted install driven from a hosted AGENTS.md. Their sequence
independently matches the architecture here (skills ship with install per
IDEA-skill-layer Tension 8, doctor closes setup per backlog 24), with GitHub
standing in as the only host.

DRAFT - Brian reviews criteria before publish.
