---
project: hall9k
type: feature
objective: A tagged commit becomes installable binaries on a bare machine - release CI, a one-line bootstrap, and the existing install path fed by artefacts instead of a local build
criteria:
- A tagged commit produces a GitHub release carrying h9k and h9kd binaries for macOS arm64 and Windows x64, built by a release workflow alongside the existing ci.yml
- A one-line bootstrap (per platform) fetches the latest release for the current platform, places the binaries in ~/.hall9k/bin, and puts h9k on PATH - with no repo checkout and no dotnet SDK on the machine; gh authentication is the stated prerequisite for a private repo's releases
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

DRAFT - Brian reviews criteria before publish.
