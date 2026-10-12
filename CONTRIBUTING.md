# Contributing to Hall9k

Thanks for your interest in Hall9k. This page is for people. If you are an agent working in this
repository, read [AGENTS.md](AGENTS.md) instead; it is the contract for agents.

## Issues first

Hall9k takes contributions issues first. Open an issue before you write code, and wait for a
maintainer to label it `help wanted` before you open a pull request for it. A pull request with no
accepted issue behind it will usually be closed, however good it is. This is not about gatekeeping:
Hall9k runs agents against its own pull requests, and every change from outside the project gets a
person's attention before anything acts on it.

- **Found a bug?** Open a bug report and include the output of `h9k --version`, your operating
  system, and the steps that reproduce it.
- **Have an idea?** Open a feature request and describe the problem before the solution.
- **Have a question?** Ask it in [Discussions](https://github.com/Hallmanac/hall9k/discussions)
  rather than in an issue.
- **Found a security problem?** Do not open an issue. Follow [SECURITY.md](SECURITY.md).

## Building and testing

You need the .NET 10 SDK. Docker is needed only for the integration tests, which run against
PostgreSQL in a container.

```bash
dotnet build
dotnet test --filter "Category!=Environment&Category!=RequiresDocker&Category!=RealProcessSpawn&Category!=PublishesBinary"
```

The filtered command is the one to run before you push. The categories it leaves out set process-wide
environment variables, Docker, real process spawning, or a published binary; CI runs the full suite on Linux
for every pull request.

The coding standards (sealed types, value objects over primitives, file-scoped namespaces, and the
rest) are in [AGENTS.md](AGENTS.md#coding-standards). They apply to people as well.

## Commits

Commit subjects start with a type and describe the change as a full sentence in the imperative:
`fix: keep a lone carriage return from becoming a line break`. The types in use are `feat`, `fix`,
`refactor`, `test`, `docs`, `chore`, and `revert`. Keep each commit buildable and about one thing.

## Changes written with an AI agent

You are welcome to use an AI coding agent to write your contribution; Hall9k exists to orchestrate
them. You own the result, though. Understand every line you submit and be ready to explain it in
review, because submitting the change is your promise that you have the right to submit it and stand
behind it. Leave AI attribution out of commits and pull requests: no `Co-Authored-By` trailers naming an
agent, and no "generated with" lines.

## Pull requests

- Link the issue the pull request resolves.
- Describe what changed and why in full sentences.
- Make sure the build passes and the filtered test command above is green.
- Expect review comments, and expect them to be specific. Disagreeing with one is fine; say why.

Pull requests merge by rebase, so the history stays linear.

## License

Contributions are accepted under the project's [Apache License 2.0](LICENSE), the same license that
covers the rest of Hall9k, as section 5 of that license provides. That means no extra certification
step and no contributor license agreement is needed.

## Code of Conduct

Everyone taking part in this project is expected to follow the
[Code of Conduct](CODE_OF_CONDUCT.md).
