===heading===
## What you are looking for
===intro===
Six areas, over the diff and every file it touches. Not a checklist to tick mechanically — read for whether this change could actually be exploited or could actually leak something, and cite the specific line or file that shows it. A change that touches none of these areas earns no findings, and that is a legitimate, complete review.
===injection===
**Injection.** Anywhere user-, network-, or file-controlled input reaches a shell command, a SQL or other query string, a deserializer, a template engine, or a path built by string concatenation, without being parameterized, escaped, or otherwise kept out of the part of the string that is interpreted as code or structure rather than data.
===secrets===
**Secrets handling.** A credential, token, key, or connection string committed in the diff itself; a secret logged, echoed, or written into a file this project does not already treat as sensitive; a secret read from an insecure source or handed somewhere it can leak from (a URL, a third-party request, an error message).
===auth===
**Authentication and authorization.** A check for who somebody is, or for what they may do, that is missing, that can be bypassed, that trusts a caller-supplied claim it should not, or that this diff weakens or moves past.
===unsafe-io===
**Unsafe process, file, or network use.** A shell command assembled from unsanitized input; a path built from input that could escape the directory it is meant to stay inside; a network call whose destination, certificate, or redirect target is not validated the way this kind of call should be.
===dependencies===
**Dependency changes.** A new or updated package this diff pulls in — read what it is, where it came from, and whether it is the kind of addition that deserves a second look before it is trusted with this project's supply chain, not only whether it compiles.
===ci===
**CI or release workflow changes.** A change to a CI workflow file, a release script, or anything else that runs with this project's own credentials or publishes on its behalf — read it for whether it now runs something it should not, hands a secret somewhere it should not reach, or lets a fork or an untrusted actor influence what a trusted workflow does.
