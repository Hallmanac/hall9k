===heading===
## The project's design system
===how-to-look===
Look for one in this repository before you decide the change ignored it. A design system here is
any of:

- design tokens — a theme file, CSS custom properties, a Tailwind or similar config, a
  `tokens.json`, a palette or type scale defined once and referenced from everywhere,
- a component library — a shared components directory, a Storybook, a published package this
  repository consumes for its own UI,
- a documented system — a design or UI guide under `docs/`, a `CONTRIBUTING`-style section on
  styling, a README that states the rules.

Search rather than assume: the tokens file is rarely named the thing you expect, and a project
can have a real system with no document at all.
===report-it===
Say what you found on its own line, exactly once:

    {{DesignSystemMarker}} <what it is and where it lives>

If you looked and this repository has none, write the word instead:

    {{DesignSystemMarker}} {{NothingWord}}

A project with no design system gets no design-system findings. That is not a gap to report as a
finding of its own; it is a fact about the project, and the report states it once at the top.
===cite-the-token===
Every design-system finding names the token or the component it concerns — the variable, the
class, the component, by its own name in this repository, and what the change used instead. "This
does not follow the design system" is not a finding anybody can act on; "this hard-codes #2F6FEB
where `--color-accent` is the token, and the two already differ in dark mode" is.
