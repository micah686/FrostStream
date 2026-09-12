# Phase 9 — encrypted local secrets evidence

Date: 2026-09-12

State: Verified, awaiting acceptance. This remains a development milestone, not a deployable Lite edition.

## Delivered behavior

`LocalFileSecretStore` implements the existing `ISecretStore` contract with NSec.Cryptography
26.4.0. Documents use XChaCha20-Poly1305 authenticated encryption with a fresh 24-byte random nonce.
The versioned envelope is authenticated against the stable purpose
`FrostStream.LocalFileSecretStore.v2` and the normalized logical secret path, so moving ciphertext
to another logical path fails authentication. The NSec symmetric master key persists separately as
an `NSecSymmetricKey` blob named `master-key.nsec`.

Logical paths retain the existing `SecretPaths` layout. Mapping is limited to eight safe segments,
512 logical characters, 128 fields, safe field names, and a configurable payload limit. Traversal,
absolute paths, malformed segments, and filesystem reparse points are rejected. Per-path semaphores
serialize writes. New ciphertext is written with write-through to a same-directory temporary file,
then atomically replaces the target. Interrupted `.tmp` files are ignored.

On Unix, secret/key directories are mode `0700` and ciphertext/master-key files are `0600`. Diagnostics and
recovery exceptions contain neither values nor resolved secret paths. Reads, replacements, deletes,
and the startup scan refuse undecryptable existing ciphertext with an instruction to restore the
matching secret directory and master key together. A new master key therefore cannot silently replace
or delete credentials encrypted with a lost key.

Lite registers this store, its startup validator, and a backup-manifest provider. The manifest names
the ciphertext directory, key directory, key filename, algorithm, and format version. The
`ISelectedOwnerSecretMigration` interface establishes the later migration boundary without exporting
plaintext yet.

Cookie routes now write content through the local store while keeping metadata in PostgreSQL and
never returning cookie bodies. Metadata failure restores the prior encrypted value. Local storage
configuration reads PostgreSQL directly, hydrates `StorageSecretSplitter` credentials from the local
store, and feeds the existing cached FluentStorage provider. Full's registration remains
`AddOpenBaoSecretStore`; no Full composition code was changed.

## Changed files

- `src/App/Shared/Secrets/LocalFileSecretStore*.cs`, `LocalSecretBackupManifest.cs`, and
  `LocalSecretStoreStartupValidator.cs`
- `src/App/Shared/Secrets/ServiceCollectionExtensions.cs`
- `src/App/Shared/Shared.csproj` and `src/Directory.Packages.props`
- `src/App/DataBridge/Lite/LocalStorageConfigClient.cs`, `CookieProfileApplication.cs`, and Lite DI
- `src/App/FrostStream.Lite/Program.cs`, configuration, capabilities, cookie controller, and storage
  status controller
- `src/App/Worker/Services/CookieMaterializer.cs` — provider-neutral error wording
- `Tests/UnitTests/Secrets/LocalFileSecretStoreTests.cs`
- `FROSTSTREAM_LITE_PHASES.MD`

## Automated verification

Commands:

```bash
dotnet restore Tests/UnitTests/UnitTests.csproj
dotnet build Tests/UnitTests/UnitTests.csproj --no-restore -m:1
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Secrets/LocalFileSecretStoreTests/*'
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Lite/LiteHostTests/*'
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.Storage/StorageSecretSplitterTests/*'
dotnet Tests/UnitTests/bin/Debug/net10.0/UnitTests.dll \
  --treenode-filter '/*/UnitTests.WebAPI/WebApiHardeningTests/*'
dotnet build src/App/FrostStream.slnx --no-restore -m:1
dotnet test --project Tests/UnitTests/UnitTests.csproj --no-restore
git diff --check
```

Results:

- Phase 9 focused tests: 15/15 passed.
- Phase 8 Lite composition tests: 7/7 passed.
- storage secret-splitting tests: 4/4 passed.
- unchanged Full WebAPI hardening tests: 12/12 passed.
- complete solution build: passed with 0 warnings and 0 errors.
- complete unit suite: 505/509 passed. The same four unrelated pre-existing failures remain:
  `CreatePolicy_Returns_Accepted_When_OpenFga_Synchronization_Is_Deferred`,
  `Every_Controller_Endpoint_Has_Detailed_OpenApi_Metadata`,
  `Authentication_Challenge_Is_Reported_As_Permanent_With_Specific_Code`, and
  `Map_Uses_Per_Comment_Unknown_Account_Handle_When_Comment_Author_Is_Missing`.

Focused coverage includes round-trip/restart, overwrite/delete, concurrent writes, malformed paths,
interrupted temporary files, malformed ciphertext, missing/mismatched-key refusal, logical-path
authentication, permissions, encrypted-at-rest inspection, backup manifest, credential hydration plus store construction, cookie
write-only responses, and cookie materialization/cleanup.

## Runtime restart verification

An initialized disposable PostgreSQL fixture and synthetic database login were used. The persistent
test root was `/tmp/froststream-phase9-runtime`; it contained separate `secrets/` and `secret-keys/`
directories. The runtime verification was repeated after the NSec migration; the persisted key was
`secret-keys/master-key.nsec`.

Launch shape:

```bash
DOTNET_ENVIRONMENT=Development \
FROSTSTREAM_STORAGE_ROOT=/tmp/froststream-phase9-runtime \
ConnectionStrings__froststreamdb='Host=127.0.0.1;Port=34500;Database=froststreamdb;Username=phase9_lite_test;Password=synthetic' \
dotnet run --no-build --no-restore \
  --project src/App/FrostStream.Lite/FrostStream.Lite.csproj -- \
  --urls http://127.0.0.1:5099
```

Smoke requests:

```bash
curl -fsS -X PUT -H 'Content-Type: application/json' \
  --data '{"content":"synthetic-cookie","site":"example.test","displayName":"Phase 9"}' \
  http://127.0.0.1:5099/api/user/cookies/phase9-smoke

# Stop and restart the same command with the same FROSTSTREAM_STORAGE_ROOT, then:
curl -fsS http://127.0.0.1:5099/api/user/cookies/phase9-smoke
curl -fsS -X PUT -H 'Content-Type: application/json' \
  --data '{"content":"synthetic-cookie-restarted","site":"example.test","displayName":"Phase 9 Restarted"}' \
  http://127.0.0.1:5099/api/user/cookies/phase9-smoke
curl -fsS -X DELETE http://127.0.0.1:5099/api/user/cookies/phase9-smoke
```

Observed: create returned metadata without content; key and ciphertext files were `0600`; a recursive
plaintext scan found no cookie value; graceful restart loaded the same NSec master key; the post-restart
overwrite decrypted the prior document and succeeded; delete returned 204. The synthetic row/login
and temporary secret/key directory were removed afterward and are not recoverable or needed.

The mismatched-key test writes ciphertext with master key A, opens the same ciphertext with unrelated
key B, and confirms read, overwrite, and delete all throw `LocalSecretRecoveryException` with the
paired-restore instruction and without secret values or physical paths. A separate missing-key test
confirms startup refuses to generate a replacement key while ciphertext exists.

## Known limitations

- The exported NSec symmetric master key is protected by `0600` permissions and host access controls,
  not an external KMS or user-supplied passphrase. Anyone who can copy both the key and ciphertext can
  decrypt the secrets. Backups must preserve both while restricting access to the pair.
- Backup/restore execution and selected-owner migration are later phases; this phase defines their
  manifest/interface only.
- Cross-process exclusive execution arrives in Phase 10. The file store serializes callers within
  the one supported Lite host; running multiple Lite hosts against one installation is unsupported.
- Storage creation/update routes remain deferred while the Phase 9 host can hydrate and construct
  existing credentialed configurations. The status route never returns hydrated parameters.

## Rollback

Remove the Phase 9 files and registrations listed above, remove the NSec.Cryptography package reference,
and restore the provider-specific wording in `CookieMaterializer`. Revert the Phase 8/9 status edits.
No schema migration was introduced. If Phase 9 has stored real Lite credentials, back up or securely
remove both configured manifest directories before rollback; retaining only one makes the other
unusable. Full OpenBao data is unaffected.
