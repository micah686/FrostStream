# Characterized workflows and remaining search contracts

| Scenario | Executable evidence | Observable result |
| --- | --- | --- |
| Download/process/playback/search | Tests/IntegrationTests/IntegrationTests/Lite/LiteRuntimeTests.cs: Download_Import_Processing_Playback_Search_Scheduling_And_Progress_Through_One_Host | Submitted job completes, durable duplicate does not create another run, history/progress arrives, original bytes and HTTP range match, actual HLS segments stream, Typesense returns indexed media. |
| Import/persistence/scheduling | Same real Lite test | Incoming/staged object scan, metadata edit/inclusion, commit/finalize, actual probing/upload; schedule hydration and completed media survive restart. |
| Lite identity/provider isolation at runtime | Same real Lite test and Tests/UnitTests/WebAPI/Auth/LiteBackendAccessTests.cs | Automatic Admin, protected endpoint access, actual Lite capabilities, no attempted connections to removed infrastructure canary. |
| Stop/restart | Stop_Cancels_Worker_And_Restart_Fences_Interrupted_Run | Stopped work stays stopped; a new run requires explicit retry. |
| Crash/redelivery | Killed_Process_Reconciles_Active_Work_And_Rejects_Stale_Durable_Redelivery | Separate process is killed; reconciliation fences the old run and handles expired durable inbox delivery. |
| Full identity/authorization | Tests/IntegrationTests/IntegrationTests/Auth/MultiUserHttpAuthTests.cs and OpenFgaAxis1FlowTests.cs | Anonymous 401, ungranted 403, Admin allow, bearer/cookie precedence, real OpenFGA tuple-based grants. Controlled signing keys replace Authentik. |
| Search parser | Tests/UnitTests/Search/AdvancedQueryParserTests.cs; Tests/UnitTests/Phase0/SearchCorpusTests.cs | Versioned query corpus exercises text, quotes, Unicode, codec aliases, numeric/date ranges, empty and unknown-field behavior. |
| Frontend access/backup presentation | Frontend/tests/lite-access.test.mjs and backup-capabilities.test.mjs | Existing logic/SSR assertions preserved; these are not browser playback or Authentik login tests. |

Internet acquisition and binary provisioning are controlled in Lite fixtures. Database, queue/workflow, FFmpeg/FFprobe, storage, indexing and media HTTP responses are real. Full authorization tests use real OpenFGA; some application request handlers are substituted. Neither suite proves a complete distributed Full media workflow.

Phase 4/5 search contracts must additionally cover metadata/library browsing, captions, comments, private notes, related media, every filter/sort, totals, deduplication, stable paging, deletions and missing documents. Use Alice/Bob ownership fixtures and require private-note isolation. Unknown field treatment is current characterization, not an irreversible product choice. Typesense syntax in the parser corpus is removed from shared tests when the typed query boundary lands; relevance-score equality is never required.

Index durability contracts must exercise committed database mutation with lost signal, index commit/replay boundaries, duplicate work, deletion, concurrent mutations/rebuild activation, cancellation and failed rebuild preserving the active generation. These are planned phase gates, not implemented Phase 0 coverage.
