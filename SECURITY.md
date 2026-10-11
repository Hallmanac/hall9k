# Security Policy

## Reporting a vulnerability

Please do not report security problems in public issues, discussions, or pull requests.

Report them privately through GitHub instead: open the repository's **Security** tab and choose
**Report a vulnerability**. Only the maintainer can see what you send. Include what you found, how
to reproduce it, and what you think an attacker could do with it.

You can expect an acknowledgement within a few days. Hall9k is maintained by one person, so a fix
may take longer than that; you will hear how it is going either way. Once a fix has shipped, the
advisory is published, with credit to you if you want it.

## Supported versions

Only the latest release receives security fixes. Update with `h9k update`.

## What is in scope

Hall9k runs AI coding agents on its users' machines, often with broad permissions, and it reads
text from GitHub to decide what those agents do. Besides the usual classes of vulnerability, these
are in scope and especially welcome:

- **Prompt injection.** Any way that text written by someone outside a project (an issue, a pull
  request, a review, a comment, a commit message) can steer an agent Hall9k launches, or can make
  Hall9k launch one at all.
- **Trust and membership.** Any way to be treated as a member of a project, or a node of an owner's
  fleet, without having been enrolled.
- **Release integrity.** Any way to make `h9k update` or the install scripts accept a build that did
  not come from this repository's release workflow.
- **Secrets.** Any way for a token, key, or credential to leave the machine it belongs to.
