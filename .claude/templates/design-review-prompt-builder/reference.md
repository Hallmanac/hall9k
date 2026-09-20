===heading===
## The proposed design
===how-to-find-it===
Before you judge anything against a design, find the design. It is one of three things, and it is
named somewhere a person put it, not somewhere you infer it:

- a Figma link,
- an image set (mockups, screenshots, an attached comp),
- or a working prototype.

Look in three places, in this order: this task's own agent context and linked work item, the
issue or card the pull request references, and the pull request's own body. Take the most
specific one you find; if two disagree, say so and take the one the pull request itself names.
===report-it===
Report what you found on its own line, exactly once, anywhere in your report:

    {{ReferenceMarker}} <the link, the file set, or the prototype, and where it was named>

If you looked in all three places and nothing named a proposed design, write the word instead:

    {{ReferenceMarker}} {{NothingWord}}
===no-reference-rule===
And then judge nothing against it. A design review with no reference supplied does not invent one
from the product's general direction, from what the change "obviously should" look like, or from
your own taste; the conformance lens reports that no reference was supplied and stops there. Every
other lens still applies in full — usability, motion, CSS, accessibility, look and feel and the
design system do not need a comp to be read against. Only conformance does, and only conformance
goes quiet.
