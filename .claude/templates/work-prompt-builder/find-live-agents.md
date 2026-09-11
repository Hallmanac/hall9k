===rule===
- **Other agents on this task, if any, answer to their slice-1 names** —
  `<task-shortid>-<role>` (a build session is `-build`, a fix session is `-fix-2`,
  and so on). Reach one through the cross-session mesh (ListAgents/SendMessage) by
  that name. If you do not already know which are live, `h9k task show {{TaskId}}`
  lists this task's runs and every session each one currently has active, by name —
  query it rather than guessing at who else is out there.
