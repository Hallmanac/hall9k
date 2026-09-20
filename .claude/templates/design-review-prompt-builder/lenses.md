===heading===
## The seven lenses
===intro===
Your report answers all seven, in this order, every time. A lens the change does not touch gets
nothing from you at all — the platform writes that lens's own one-line stand-in when you say
nothing under it, so silence is safe and padding is not. What is never acceptable is a lens
answered with something you did not actually look at.
===user-experience===
### User experience

Whether a person can do the thing. Walk the flow the change touches from its real entry point,
not from the component in isolation: what does someone see first, what can they do next, what
happens when they do the wrong thing. Empty states, loading states, error states, and the state
after a slow network are part of the flow, not edge cases. A control that is discoverable only if
you already knew it was there is a finding.
===proposed-design===
### Conformance to the proposed design

Whether what shipped is what was drawn. Compare against the reference you found, concretely:
layout, hierarchy, spacing, copy, states, and what is present at all. Name the divergence and
where the reference says otherwise. A deliberate, better departure from the comp is worth saying
out loud as a departure rather than passing over silently; the person who drew it gets to decide
which it was.
===motion===
### Motion (transitions and animations)

What moves, for how long, and why. Read the transitions and animations the change adds, removes,
or alters: their duration and easing against whatever the rest of the product uses, whether
anything animates a property that forces layout, whether a state change that should be
perceptible is instant and whether one that should be instant is animated. Check that reduced
motion is honoured — a `prefers-reduced-motion` query, or the project's own equivalent — and say
plainly when it is not.
===css-practice===
### CSS practice

How the styling is written. Specificity fights and `!important`; magic numbers where a token or a
scale exists; duplicated rules that will drift; layout done by hand where the project already has
a grid or flex pattern; dead rules the change left behind; selectors coupled to markup structure
that will break on the next refactor. Read it as code somebody maintains, because it is.
===accessibility===
### Accessibility

Whether a person who is not using a mouse and a bright screen can do what the change added. Focus
order and visible focus; keyboard reachability of everything clickable; labels and accessible
names on controls and inputs; heading structure; contrast against the change's own background;
live regions for anything that updates without navigation; the semantics of the element actually
used versus the one that behaves that way. Judge it yourself, always, and say what you judged.
===look-and-feel===
### Look and feel

The visual judgment nobody else on this review is going to make. Type scale and weight, rhythm
and spacing, alignment, density, colour and contrast as aesthetics rather than as compliance,
and whether this screen looks like the rest of the product or like a different product bolted on.
Say what is wrong and what you would do instead; a taste finding without a proposal is not much
use to the person reading it.
===design-system===
### Design system

Whether the change spends the system rather than working around it. A hard-coded value where a
token exists; a one-off component where a shared one does; a shared component forked or
overridden instead of extended or fixed at the source; a new pattern that duplicates one already
in the library under another name. If the change genuinely needs something the system does not
have, that is worth saying too — a gap in the system is a finding about the system.
