===lead-in-mid-sentence===
and it names
===lead-in-sentence-head===
That range names
===reason===
  {{LeadIn}} this branch's recorded fork point off `{{BaseBranch}}` as a literal
  commit rather than `origin/{{BaseBranch}}`: this branch is stacked on that one, and a
  parent branch force-pushed while this review runs — an ordinary review lap folding
  fixes into its own commits — moves that ref out from under the range, folding the
  parent's whole rewritten-away delta into what would read as this branch's work.
  Do not substitute `origin/{{BaseBranch}}` back in, and do not compute the boundary with
  `git merge-base`: a force-push collapses that too.
