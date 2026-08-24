---
project: hall9k
type: feature
objective: The daemon's operating settings survive the shell that started it - concurrency, model roles, and their kin live in durable per-install config the daemon reads itself, so a fresh machine or an autostart launch runs with the operator's settings and no environment-variable ritual
criteria:
- Daemon options that today only bind through environment variables (Hall9k__MaxConcurrentAgentSessions, Hall9k__ModelByRole__*, and any sibling DaemonOptions members) also load from the platform config file at ~/.hall9k (the same file the connection-string chain already reads), with precedence environment over config file over built-in default, so an env var stays a one-off override rather than the only way
- The settings are written and read through the CLI (h9k config set / h9k config show, or the closest fit to the existing command surface) with the same teach-first help standard every command carries; hand-editing the file also works and survives, because the file is the record
- h9k daemon status names where each effective setting came from (env, config, default), so a wrong-quoting or wrong-file mistake is diagnosable in one command instead of a stale-log hunt
- A daemon launched by autostart (launchd today, the Windows logon task when it lands) picks up the config-file settings with no environment at all - this is the case env vars structurally cannot serve
- The install and update paths never overwrite an existing config file; a missing one is created with defaults on first need, stated out loud
- Documentation follows the code: INSTALL.md and docs/operations.md name the config file and the precedence chain
- dotnet build and dotnet test pass
---
Origin (2026-08-23, two incidents in one day): the daemon went down for 30
minutes because a shell-quoting mistake in the env-var ritual made the start
command a no-op that read as success from a stale log line; and every single
daemon start since the role-split experiment began has had to re-pass three
ModelByRole variables plus the concurrency ceiling by hand, because nothing
durable holds them. The autostart path makes this structural: a launchd agent
or Windows logon task has no operator shell to export anything, so today
autostart necessarily runs with defaults that silently drop the operator's
model policy. Decision #33's model-resolution chain gains its missing bottom
layer: task override, then role, then project, then platform default - where
role and default finally live somewhere that survives a reboot.

Scope note: this is settings durability only, on the machine's own install.
Nothing here touches the shared-database or peer-to-peer future; a config
file per install is compatible with every room shape ruled at discovery.
