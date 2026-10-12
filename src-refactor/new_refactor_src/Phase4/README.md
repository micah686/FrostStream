# Phase 4: complete search boundary, retaining Typesense

All implementation is in `new_refactor_src`. Lite and Full still register the isolated Typesense integration; Phase 5 will replace Lite's registration and remove its Typesense deployment dependency.

The DataBridge module now owns advanced parsing, result/card mapping, library browsing, unified scoped search, similar media, comment and caption handlers, authoritative projection sourcing, caption-sidecar hydration, durable projection execution, and rebuild orchestration. The adapter owns only the SDK, schema definitions, query translation, generation storage, and Typesense connectivity/bootstrap.

The provider-independent ports are `ISearchReader`, `ISearchIndexWriter`, `IMediaDocumentQuery`, and `IMetadataRebuildCoordinator`, with `SearchStatus` for lifecycle state. `SearchRequest`, `SearchFilter` (text/numeric predicates and boolean groups), `SearchSort`, and `SearchPage<T>` contain no vendor types or query-language strings. Advanced parsing retains aliases, dates, ranges, quoted values, Unicode, and unknown-token behavior. Historical filter-string characterization now checks the adapter's translation separately from the typed parser.

`GET /api/search/status` reports building, ready, degraded, or failed through DataBridge request/reply, including in Full where the API is a separate process. The route uses the metadata authorization bundle. Rebuild errors propagate to durable job execution rather than being logged as successful work. Missing caption sidecars fail replacement construction instead of silently activating incomplete caption text.

Rebuilds create uniquely named media/comment/caption collections and populate them without clearing the active collections. One persistent `froststream_search_active` alias points at the media collection of the active generation; the other collection names derive from that same generation. Activation validates all three collections, changes one alias, and updates the in-process router. Restart resolves the same generation from that alias. Failed/canceled generations are discarded; potentially active generations are retained if a network failure makes activation uncertain. Previous active generations remain available for recovery. Successful generation retention currently requires operational cleanup; automatic generation pruning is future maintenance work.

The existing writer barrier excludes incremental projection execution during construction. Revision intent is invalidated before construction, replayed in bounded pages into the replacement before activation, and never acknowledged by the rebuild. Mutations racing replay stay pending for the revision worker after activation, including deletions. This preserves Phase 3's durable eventual-consistency model. A database mutation barrier providing a strict database snapshot at activation remains Phase 5 lifecycle work; the current gate does not prevent application database writes.

Intentional behavior fixes: explicit subtitle/comment scopes no longer include unrelated metadata hits; private-note matching uses parsed free text and the same structured filters as other matches; title sorting is enabled; media results have an explicit stable ID tie-breaker. Existing unified response semantics are retained: metadata results are primary and paginated, while bounded content/note matches augment those results with deduplicated match reasons. `TotalCount` includes that page's new augmentation and `HasMore` follows the primary metadata result. This is not a globally paginated union or a global count of all matching content documents. The boundary exposes this existing application behavior without claiming broader search semantics or relevance-score parity.

Verification:

```sh
python new_refactor_src/Phase4/verify-boundaries.py
dotnet build new_refactor_src/FrostStream.slnx --no-restore
# Run against an owned, disposable Typesense 30.2 server with API key phase4-test-key.
FROSTSTREAM_TEST_TYPESENSE=http://127.0.0.1:24104 \
  dotnet run --project new_refactor_src/Tests/UnitTests/UnitTests.csproj --no-restore
```

`TypesenseContractTests` opts into real-server tests when `FROSTSTREAM_TEST_TYPESENSE` is set. Without it, the external-server scenarios are not exercised. Both edition adapter configurations run the same real query/writer contract and shared handlers; no Full deployment or real Authentik/NATS/PostgreSQL end-to-end coverage is implied by those tests. Fixtures use an owned server and replace its active alias, so do not point them at an application instance. They exercise advanced filters, Unicode, browsing/paging/sorts, scopes, parent content matches, deduplication, private-note isolation/filtering, related media, comment/caption listings, deletion, status, partial import failure, restart routing, rebuild failure/cancellation, and pending mutation/deletion replay.

Phase 5 supersedes the temporary Lite Typesense exception: Lite now uses embedded Lucene and Full retains Typesense. See `../Phase5/README.md` for current composition and verification.
