# Storage Credentials with OpenBAO

This document describes how FrostStream protects sensitive storage backend credentials (SMB/NFS/FTP/SFTP passwords and SSH keys, S3 access/secret keys, Azure account keys / connection strings / SAS URLs, and GCS service-account JSON) using **OpenBAO** as a secret store.

## Goals

1. Sensitive values are never persisted in plaintext in the application database.
2. Sensitive values are never returned in WebAPI responses.
3. The application can still construct `IBlobStorage` instances on demand using those credentials.
4. Admin UX for create / update / delete remains workable.

## Why OpenBAO and not envelope encryption?

The original proposal suggested **envelope encryption** (Vault Transit wrapping a per-record DEK, ciphertext stored alongside the data). For FrostStream's scale — tens of storage configs, low read frequency — that pattern is unnecessarily heavyweight. Storing secrets directly in **OpenBAO KV v2** with a path reference in Postgres gives the same protection model with fewer moving parts:

| Property                                | KV-v2 + path reference | Envelope (Transit + ciphertext in DB) |
|-----------------------------------------|------------------------|----------------------------------------|
| Plaintext absent from app DB            | ✅                     | ✅                                     |
| DB-only access can't read secrets       | ✅                     | ✅                                     |
| Access gated by Vault policy + audit    | ✅                     | ✅                                     |
| "Write-only admins" enforceable         | ✅ (KV policy)         | ✅ (Transit + KV policy)               |
| Versioning / rollback for free          | ✅ (KV v2)             | ❌ (you build it)                      |
| Operational complexity                  | Low                    | Medium-high (DEK lifecycle, rewrap)    |

Envelope encryption would only become attractive if you had bulk data to encrypt or a regulatory driver requiring plaintext never transit Vault. Neither applies here.

## High-level architecture

```
                    ┌────────────────────────────────────────┐
   admin POST       │                                        │
   /api/storage/... │                       ┌──── OpenBAO ───┤
       (plaintext)  │                       │   KV v2 mount  │
            │       │                       │   secret/      │
            ▼       │                       │     storage/{key}
   ┌──────────────┐ │   NATS request/reply  │                │
   │   WebAPI     │─┼──────────────►┌───────┴────────┐       │
   │ (controllers)│ │               │  DataBridge    │ writes│
   └──────────────┘ │               │  CRUD consumer │──────►│
                    │               │  (writes both) │       │
                    │               └────────────────┘       │
                    │                       │                │
                    │                       ▼                │
                    │              ┌────────────────┐        │
                    │              │   Postgres     │        │
                    │              │  storage_keys  │        │
                    │              │  (no secrets)  │        │
                    │              └────────────────┘        │
                    │                                        │
                    │     consumer reads at use time:        │
                    │     ┌────────────────┐                 │
                    │     │ Worker /       │ DTO + secrets   │
                    │     │ MediaProcessor │◄──── Vault ◄────┤
                    │     │                │ via             │
                    │     │ IBlobStorage   │ IStorageConfig  │
                    │     │ Provider       │ Client          │
                    │     └────────────────┘                 │
                    └────────────────────────────────────────┘
```

The application database stores **only non-sensitive metadata** — protocol, host, port, username, bucket, region, container name, credential mode, etc. The Vault path is **derived** from the storage key (`secret/storage/{key}`); no `vault_path` column is required.

## Components

### `Shared.Secrets`

| Type                         | Role                                                                     |
|------------------------------|--------------------------------------------------------------------------|
| `ISecretStore`               | Abstraction: `Read`, `Write`, `Delete` keyed by path.                    |
| `OpenBaoSecretStore`         | `VaultSharp`-backed implementation against KV v2.                        |
| `OpenBaoOptions`             | `Address`, `Token` (dev) or `RoleId`+`SecretId` (AppRole), `KvMount`.    |
| `SecretPaths.ForStorage(key)`| Returns `"storage/{key}"` — composed against the configured KV mount.    |
| `AddOpenBaoSecretStore()`    | DI registration extension.                                               |

`VaultSharp` is API-compatible with OpenBAO since OpenBAO retains Vault's HTTP surface.

### `Shared.Storage` — input vs stored shapes

The original parameter classes (`StreamingNetworkStorageParameters`, `S3CompatibleObjectStorageParameters`, `AzureBlobObjectStorageParameters`, `GoogleCloudStorageObjectStorageParameters`) remain the **input** shapes — used on POST/PUT bodies and on the create/update NATS messages, where plaintext arrives.

A parallel hierarchy of **stored** shapes (`StorageParametersStoredBase`, `StreamingNetworkStorageStored`, `S3CompatibleObjectStorageStored`, `AzureBlobObjectStorageStored`, `GoogleCloudStorageObjectStorageStored`, plus `PosixLocalStorageStored`) holds only non-sensitive fields and is what gets persisted, returned to admin clients, and carried over the message bus in `StorageConfigDto`.

### `StorageSecretSplitter`

Two operations:

- `Split(input)` → `(IReadOnlyDictionary<string,string> secrets, StorageParametersStoredBase stored)` — pulls sensitive fields out of an input parameters object so the consumer can write them to Vault and persist the stored half.
- `Hydrate(stored, secrets)` → input parameters — re-merges secrets fetched from Vault back onto a stored shape so `FluentStorageProvider` can build a connection string unchanged.

### `IStorageConfigClient` / `NatsStorageConfigClient`

Resolves a storage key to a fully hydrated `StorageConfigResponse` (input parameters JSON, with secrets merged in). Workflow:

1. NATS request to `storage.get` → DataBridge returns the stored DTO.
2. Calls `ISecretStore.ReadAsync(SecretPaths.ForStorage(key))`.
3. `StorageSecretSplitter.Hydrate(stored, secrets)` produces the input variant.
4. Serialised parameters JSON returned in `StorageConfigResponse.Parameters`.

### `IBlobStorageProvider` / `CachingBlobStorageProvider`

Per-key cache of `IBlobStorage` instances backed by `IStorageConfigClient` + `FluentStorageProvider`. `Invalidate(key)` evicts and disposes the previous instance.

### `StorageConfigChangedSubscriber`

Hosted service subscribing to `storage.changed` (`StorageConfigChangedMessage`) on NATS. On any create / update / delete, it invalidates the corresponding cache entry across every host that has the subscriber registered.

### `DataBridge.StorageCrudConsumerService`

The single source of truth for storage config writes. For each create / update it:

1. Validates the input parameters.
2. `StorageSecretSplitter.Split(input)` → `(secrets, stored)`.
3. **Vault-write first** so the invariant *"DB row exists ⇒ Vault path exists"* always holds.
4. `entity.ApplyStoredParameters(stored)` and `SaveChangesAsync()`.
5. On DB failure, best-effort `secretStore.DeleteAsync(...)` to avoid orphan secrets.
6. Publishes `StorageConfigChanged` so caches across the cluster evict.

On delete, the DB row goes first (it is the source of truth), then `secretStore.DeleteAsync(...)` runs as a best-effort cleanup. Orphan secrets in Vault are tolerable; missing secrets while a DB row exists are not.

On update, if no sensitive fields are supplied this round (e.g. switching from password to key-based auth), the prior secret bundle is deleted so stale values don't linger.

## Postgres schema

Migration **001** (unchanged) creates the parent `storage_keys` table and the per-method child tables (`storage_keys_local`, `storage_keys_network`, `storage_keys_object_s3_compatible`, `storage_keys_object_azure_blob`, `storage_keys_object_google_cloud_storage`).

Migration **002** (`002_DropPlaintextStorageCredentials.cs`) removes every plaintext credential column:

| Table                                       | Columns removed                                                                       |
|---------------------------------------------|---------------------------------------------------------------------------------------|
| `storage_keys_network`                      | `password`, `private_key`, `public_key`                                               |
| `storage_keys_object_s3_compatible`         | `access_key_id`, `secret_key_id`, `session_token_secret_id`                           |
| `storage_keys_object_azure_blob`            | `azure_account_key_secret_id`, `azure_connection_string_secret_id`, `azure_sas_url_secret_id` |
| `storage_keys_object_google_cloud_storage`  | `gcp_credentials_json`, `gcp_credentials_json_is_base64_encoded`                      |

It also adds `storage_keys_object_s3_compatible.has_session_token BOOLEAN NOT NULL DEFAULT false` so admin UIs can render a "session token set" indicator without a Vault round-trip on list.

`Down()` reverses the change — note that in production the plaintext values cannot be reconstructed from Vault if you've already discarded the old DB columns, so a real downgrade requires re-entering credentials.

After migration the on-disk schema for storage credentials looks like this:

```
storage_keys (id, key, method, description, created_at, last_updated)
├── storage_keys_local (storage_key_id, protocol, path)
├── storage_keys_network (storage_key_id, protocol, host, port,
│                         username, base_path)
├── storage_keys_object_s3_compatible (storage_key_id, provider,
│                         bucket_name, region, endpoint,
│                         has_session_token, force_path_style, use_ssl)
├── storage_keys_object_azure_blob (storage_key_id, credential_mode,
│                         container_name, azure_account_name)
└── storage_keys_object_google_cloud_storage (storage_key_id, bucket_name,
                              credential_mode, gcp_credentials_file_path,
                              gcp_project_id)
```

A `psql` user with full read access to `froststreamdb` cannot read any storage credential. The most they can do is enumerate which storage backends exist and against which hosts/buckets they point.

## Vault layout

Each storage key has exactly one secret at:

```
secret/data/storage/{key}
```

(KV v2 prepends `data/` to writes; reads use the same logical path.) The fields stored under that path depend on the storage method:

| Method                  | Fields stored in Vault                                                                |
|-------------------------|---------------------------------------------------------------------------------------|
| Local                   | (none — no secrets)                                                                   |
| Network                 | `password`, `privateKey`, `publicKey` (any subset — only non-empty fields are written) |
| Object — S3-compatible  | `accessKeyId` (required), `secretKeyId` (required), `sessionToken` (optional)         |
| Object — Azure Blob     | One of `azureAccountKey`, `azureConnectionString`, `azureSasUrl` (per credential mode) |
| Object — GCS            | `gcpCredentialsJson` + `gcpCredentialsJsonIsBase64Encoded` (when mode is `CredentialsJson`) |

KV v2 versioning is left at its defaults — every update creates a new version, with rollback available via `bao kv get -version=N` or the API.

## AppHost wiring

OpenBAO runs as an Aspire container resource:

```csharp
const string baoDevRootToken = "froststream-dev-root";
var openbao = builder
    .AddContainer("openbao", "openbao/openbao", "latest")
    .WithHttpEndpoint(port: 8200, targetPort: 8200, name: "http")
    .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", baoDevRootToken)
    .WithEnvironment("BAO_DEV_LISTEN_ADDRESS", "0.0.0.0:8200")
    .WithArgs("server", "-dev", "-dev-root-token-id", baoDevRootToken);

var openbaoEndpoint = openbao.GetEndpoint("http");
```

Each service receives `OpenBao__Address` (mapped to `OpenBaoOptions.Address`) and `OpenBao__Token` env vars. In dev mode the KV v2 engine at `secret/` is enabled by default and the deterministic root token short-circuits AppRole, so no init step is needed.

Production deployments should:

- Run OpenBAO with persistent storage and proper unsealing — **not** `-dev`.
- Replace `Token` with `RoleId` + `SecretId` (AppRole) under `OpenBaoOptions`.
- Apply a Vault policy granting `create / read / update / delete` on `secret/data/storage/*` and `secret/metadata/storage/*` — split into separate roles if you want write-only admins.

## Service registration

| Service     | Registers                                       | Notes                                                                                   |
|-------------|-------------------------------------------------|-----------------------------------------------------------------------------------------|
| WebAPI      | (uses NATS-only path; no `ISecretStore` needed) | Storage CRUD goes through NATS to DataBridge; WebAPI never touches Vault directly.       |
| DataBridge  | `AddOpenBaoSecretStore()`                       | Owns Vault writes via `StorageCrudConsumerService`.                                     |
| Worker      | `AddOpenBaoSecretStore()`                       | `AddFrostStreamStorage()` is left commented out until Worker actually consumes `IBlobStorageProvider` (it requires NATS wiring in Worker first). |

`AddFrostStreamStorage()` (in `Shared.Storage.ServiceCollectionExtensions`) registers `IStorageConfigClient`, `IBlobStorageProvider`, and the `StorageConfigChangedSubscriber` hosted service. It depends on both `IMessageBus` (NATS) and `ISecretStore`.

## End-to-end flows

### Admin creates an SFTP storage config

1. `POST /api/storage/network/create` with `{ key, host, port, username, privateKey, basePath, ... }`.
2. WebAPI publishes `StorageCreateStreamingRequestMessage` over NATS.
3. DataBridge consumer:
   - Validates input.
   - `StorageSecretSplitter.Split` → `secrets = { privateKey: "..." }`, `stored = { protocol, host, port, username, basePath }`.
   - `secretStore.WriteAsync("storage/{key}", secrets)`.
   - Persists the stored shape to `storage_keys` + `storage_keys_network`.
   - Publishes `StorageConfigChanged { Created }` on `storage.changed`.
4. Response to admin contains stored fields only — no secret material.

### Worker pulls a file via that storage config

1. Worker calls `IBlobStorageProvider.GetAsync(key)`.
2. Cache miss → `IStorageConfigClient.GetStorageConfigAsync(key)`:
   - NATS `storage.get` returns the stored DTO.
   - `ISecretStore.ReadAsync("storage/{key}")` returns the secret bundle.
   - `StorageSecretSplitter.Hydrate(stored, secrets)` reconstructs the input parameters.
3. `FluentStorageProvider.CreateStorage(...)` builds the connection string and yields an `IBlobStorage`.
4. The `IBlobStorage` is cached for subsequent requests with the same key.

### Admin rotates the SFTP private key

1. `PUT /api/storage/network/update/{key}` with the new `privateKey`.
2. DataBridge writes the new secret bundle to `secret/data/storage/{key}` — KV v2 records this as version 2.
3. DataBridge publishes `StorageConfigChanged { Updated }`.
4. Every Worker / MediaProcessor instance subscribed via `StorageConfigChangedSubscriber` evicts its cached `IBlobStorage` for that key. Next call rebuilds with the rotated credential — no restart.

### Admin deletes a storage config

1. `DELETE /api/storage/delete/{key}`.
2. DataBridge removes the row from `storage_keys` (cascades to the per-method child).
3. Best-effort `secretStore.DeleteAsync("storage/{key}")` — KV v2 metadata + all versions removed.
4. `StorageConfigChanged { Deleted }` published; caches evict.

## Properties this gives you

- **Postgres dump or read-only DB role does not leak credentials.** A backup, replica, or BI query can see only protocol/host/bucket metadata.
- **Admins with DB access alone cannot snoop.** They need a Vault token / AppRole to read secrets, and that path is auditable.
- **API responses are safe to log or screenshot.** The WebAPI controllers and `StorageConfigDto` no longer carry password/private key/access key/secret key/SAS URL/GCS JSON fields. Only `HasSessionToken` remains as a UI hint.
- **Rotation does not require a redeploy.** KV v2 versioning + cache invalidation is sufficient.
- **DB row ⇒ Vault path** invariant is always preserved (Vault-write before DB-write, Vault-delete after DB-delete with best-effort cleanup).

## Known gaps

- **WebAPI is still unauthenticated.** `StorageController` has no `[Authorize]` and no auth scheme is configured. The "admins vs moderators" distinction the original requirement hinted at requires authentication and is intentionally deferred. Track this as a follow-up — until it's addressed, anyone who can reach the WebAPI port can create or overwrite storage configurations even though they can no longer read existing secrets.
- **Dev mode uses a single root token.** Production must switch to AppRole and proper unsealing. The `OpenBaoOptions` shape already supports both — only `AppHost` and ops configuration need updating.
- **No automatic rewrap on key rotation.** Because nothing in the app DB is encrypted, there is nothing to rewrap. If you ever add envelope encryption later, this becomes a real concern.

## Verification checklist

After applying migration 002 against a clean dev DB:

1. **Local storage** — `POST /api/storage/local/create` with a path; confirm the response shape is unchanged. (No secrets path; regression check.)
2. **Network (SFTP)** — POST with username + private key. Then:
   - `psql` `\d storage_keys_network` → no `password` / `private_key` / `public_key` columns.
   - `bao kv get secret/storage/{key}` → values present.
   - `GET /api/storage/{key}` → response contains only host/port/username/basePath.
   - Trigger an ingest job using this storage → file lands successfully.
3. **S3-compatible** (e.g. MinIO) — same pattern: DB row has no key columns, Vault has them, ingest works.
4. **Azure Blob (account key mode)** — DB clean, Vault has key, blob upload works against Azurite.
5. **GCS (CredentialsJson mode)** — DB clean, Vault has the JSON, blob upload works.
6. **Update** — PUT new credentials → `bao kv metadata get` shows version 2; `-version=1` still returns the old value.
7. **Delete** — DELETE → DB row gone, `bao kv get` returns `not found`.
8. **Cache invalidation** — while Worker is running, update a config; next operation against that key picks up new credentials with no restart.
9. **Restart resilience** — restart all services; storage list still resolves and ingest still works.
10. **Negative path** — stop the OpenBAO container, attempt an ingest using a non-cached key; expect a clear logged error rather than a silent fall-back.
