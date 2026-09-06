# Sessions spawned by the arx-platform project orchestrator

Outside the task lifecycle only; the daemon's dispatched sessions are in the database. Liveness
comes from `ListAgents`. Never resume; the last column opens a fresh session on the same subject.

| Started | Kind | Subject | Status | Fresh start |
|---|---|---|---|---|
