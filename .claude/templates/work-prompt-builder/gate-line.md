===line===
  - `{{Command}}`
===host-coupled-line===
  - `{{Name}}` runs only in the daemon's own serialized host gate; this session never runs it
    here — the one exception is a single touched test class, scoped by name (e.g.
    `dotnet test --filter "FullyQualifiedName~ThatClass"`), never the gate's own full command.
