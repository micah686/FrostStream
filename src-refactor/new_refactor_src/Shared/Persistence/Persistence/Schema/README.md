# Schema initialization and restart

Full initializes a checksummed fresh-install PostgreSQL catalog in one transaction under an advisory lock. Existing populated unmarked databases and unknown/changed baseline histories are rejected. Historical migration replay has been removed. Lite initializes the immutable SQLite v1 baseline and reviewed v2–v4 steps. Workflow schemas have independent histories: pinned Cleipnir owns Full; the Lite workflow store owns its SQLite v1 tables.

Both editions reconcile durable download runs before processing starts. Restart does not invent a new user-requested run. SQLite rebuild/evolution tests verify data, constraints, rollback, cancellation and independent workflow history. Full tests verify fresh initialization, repeated initialization, preserved edits and checksum refusal. Packaging and restore procedures are documented in [Phase 6](../../../../Phase6/README.md).
