# PostgreSQL fresh-install baseline v1

`001-baseline.sql` is the normalized PostgreSQL 18.3 catalog of a fresh M001–M098 installation, including final seeds and search intent/projection triggers. It excludes Cleipnir runtime tables, which the pinned workflow store initializes independently. Capture used the owned Phase 6 schema container, not developer or production data. pg_dump ownership/ACL directives, psql-only guards and session settings were removed; captured seed timestamps were replaced with `CURRENT_TIMESTAMP`. The initializer recomputes cron due times from installation time.

The legacy `VersionInfo` rows document the capture cutoff; normal initialization uses `froststream_installation_baseline` version/checksum and does not replay old migrations. The complete SQL resource is hashed before use. Baseline DDL/seeds/checksum are one transaction, serialized with an advisory lock. Populated unmarked databases and changed/unknown baseline histories are rejected. This is a fresh-install boundary; no migration from previous phases is promised.

Real PostgreSQL unit contracts verify initialization, repeatability, preserved edits and checksum rejection. Packaged Full recovery verifies that this catalog survives physical pgBackRest backup/restore. See `docs/implementation-history/README.md` for operational scope and recovery.
