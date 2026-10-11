===line===
  - `{{Command}}`
===host-coupled-line===
  - `{{Name}}` runs only in the daemon's own serialized host gate; this session never runs it
    here — the one exception is a single touched test class, scoped by name (e.g.
    `dotnet test --filter "FullyQualifiedName~ThatClass"`), never the gate's own full command.
===unaccepted===
  - This project's verify gate set has changed and has not yet been accepted on this node, so
    its commands are not listed here — do not run them yourself. The platform's own
    verification holds at gate entry until this node's operator accepts the current set
    (`h9k project accept-gates`); commit your work and finish normally, and let that
    verification run once it is accepted.

===path-scope===
{{Indent}}  Scoped to the paths: {{PathSet}}
{{Indent}}  Run this gate only when this branch's changes against its base, committed or not, touch a
{{Indent}}  path that set matches; a `!` entry excludes and wins over any other entry. When you are
{{Indent}}  unsure whether a path matches, run it. Otherwise leave it to the daemon's own verification.
{{Indent}}  A glob ending in `/` matches everything under that directory from the repository root;
{{Indent}}  one with no `/` matches a file name at any depth; otherwise `**` crosses directories and
{{Indent}}  `*` does not.
