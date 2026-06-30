# Core Backup And Restore

FrostStream core backups contain only the data needed to recreate an instance:

- PostgreSQL logical dumps for `froststreamdb`, `authentikdb`, and `openfgadb`
- OpenBao KV v2 secret data from the configured mount
- restore requirements and checksums

Media files, local import source files, Typesense data, NATS runtime state, and worker caches are intentionally excluded.

## Create A Backup

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  create \
  --output /var/backups/froststream \
  --name froststream-core-$(date -u +%Y%m%d%H%M%S) \
  --postgres-host localhost \
  --postgres-port 5432 \
  --postgres-user postgres \
  --openbao-address http://127.0.0.1:8200 \
  --openbao-kv-mount secret
```

Set `POSTGRES_PASSWORD` and `OPENBAO_TOKEN` in the environment rather than passing them on the command line.

## Verify A Backup

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  verify \
  --archive /var/backups/froststream/froststream-core-20260628010203
```

## Restore

Restore is a cold/offline operation.

1. Stop WebAPI, DataBridge, Worker, Scheduler, Authentik, OpenFGA, and OpenBao.
2. Ensure PostgreSQL and OpenBao are reachable.
3. Run:

```bash
dotnet run --project src/App/BackupTool/BackupTool.csproj -- \
  restore \
  --archive /var/backups/froststream/froststream-core-20260628010203 \
  --force
```

4. Restart services.
5. Trigger a metadata search reindex so Typesense is rebuilt from PostgreSQL.

## WebAPI Admin Surface

When `Backup` options are configured, WebAPI exposes:

- `POST /api/admin/backups`
- `GET /api/admin/backups`
- `GET /api/admin/backups/jobs`
- `GET /api/admin/backups/jobs/{jobId}`
- `POST /api/admin/backups/verify`
- `POST /api/admin/backups/restore-plan`

The API can start and verify backups, but it does not perform restore. `restore-plan` returns the offline CLI command operators should run after stopping services.
