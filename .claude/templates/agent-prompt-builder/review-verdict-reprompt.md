===intro===
Your review session ended without the required VERDICT line, or with a
{{NeedsFixesWord}} verdict naming nothing the platform could read as a finding — either
way, the platform could not read your judgment. This does not mean a finding you
stated was wrong: it means the platform's automatic reader could not recognize a
location and a defect in how you wrote it. Conclude now:
===wait-for-checks===
- If any checks or commands are still unfinished, wait for them and fold the
  results into your judgment.
===restate-finding===
- If you still believe a finding stands, restate it in full and in the header
  contract below, as plainly as you can — the platform reads this message in place
  of your earlier one, so a finding restated without its FINDING header arrives
  ungraded and unplaced, and its severity and scope are lost. Only return
  {{MergeReadyWord}} if, on reconsideration, you no longer believe any defect stands —
  not merely because restating it once more feels repetitive.
===fix-bar-final-full-pass===
- A {{NeedsFixesWord}} verdict must name at least one finding: a stated location (a file,
  or a file and line) and a description of the defect there, graded high — this is
  the mandatory final pass, so its own bar is high alone (Decisions Log #119); a
  medium-, low-, or ungraded-only finding still belongs in your answer, attached
  under a {{MergeReadyWord}} verdict rather than a {{NeedsFixesWord}} one.
===fix-bar-ordinary===
- A {{NeedsFixesWord}} verdict must name at least one finding: a stated location (a file,
  or a file and line) and a description of the defect there, graded medium or high —
  a low-only or ungraded finding still belongs in your answer, attached under a
  {{MergeReadyWord}} verdict rather than a {{NeedsFixesWord}} one.
===end-with-verdict-line===
- End your final message with exactly one verdict line, nothing after it:
  `{{VerdictMarker}} {{MergeReadyWord}}` or `{{VerdictMarker}} {{NeedsFixesWord}}`.
===closing===
This is the only re-prompt this review cycle receives; ending without a verdict
again hands the run to a human. This is still review cycle {{Cycle}} for this run.
