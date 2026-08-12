# FrostStream

![Froststream-logo](assets/froststream-banner.svg)

**A self-hosted media archiving and streaming server.** 

Froststream is a self-hosted media archival tool and library. It downloads music and video files (via yt-dlp), and presents the media files in a youtube-like web interface.  
This allows for a personal media collection that can't be removed by corporations.  

The application is entirely designed to be accessed by REST, so clients can be made easily for interacting with FrostStream, the same way the web UI interacts.  

> [!WARNING]  
> This application is under heavy development right now. Data loss may occur, and features/capabilities may change

---

### AI Notice
This application has been vibe coded, though it has been guided to follow an architectural design/structure.  
Once more of the core features have been implemented,the plan is to rework some of the logic to hand-edited code.  
(I would not be able to develop an application of this size by myself otherwise).   

---

## Features

### Downloading & Archiving

- **One-off downloads**: Submit any yt-dlp-supported URL for video or audio-only download, with optional stored option presets
- **Creator sources**: subscribe to channels/playlists; a hybrid scheduler (global tick + per-source check interval) automatically picks up new uploads, with an on-demand "scan now"
- **Playlist handling**: playlist URLs are detected and split out into per-item download jobs
- **Download queue**: live queue/detail/history views with server-sent-event progress updates
- **Cookies & PO tokens**: per-site cookie storage (kept in OpenBao, per user scroped) and a built-in [bgutil POT provider](https://github.com/Brainicism/bgutil-ytdlp-pot-provider) broker so YouTube downloads don't get blocked
- **Storage-affine workers**:  Use tags to assign jobs to workers with a specified tag
- __Multiple Storage Targets__ : Instead of being restricted to a single storage solution (local/FTP/S3), you can download media to the storage depending on your needs (per media). You can even download the same media to multiple storage solutions (for example, local vs a warm archive, like a NAS)
- __Multiple Media Editions__: If a media has multiple versions from the same url (like if a creator had updated a youtube video), it keeps *both* copies of the media

### Library/Metadata

- **Atomic ingestion**: All downloads are hashed (XxHash128), verified, and committed to the catalog with a trust model  
- **Bulk import** (in progress): scan → probe → review → commit pipeline for migrating an existing media folder, with metadata enrichment. Plex/TubeArchivist support planned for a future release  
- **Full-text search** — Typesense-backed search over media, comments, and captions (typo-tolerant), along with an advanced search. Local LLM/Embedding for search may be added for a future release, depending on demand.  
- **Durable media assets** — thumbnails, captions, avatars, and banners are stored alongside media  
- **Live Chat Playback** - Optionally ingest and replay archived live chat through ClickHouse.  

### Playback

- **Browser playback**: Playback media via the web (desktop or mobile)
- **Server-side casting**: cast to remote devices. FCast and browser-based Chromecast should work for now, dedicated Chromecasting will come later
- **Live chat replay** (optional): archived YouTube live chat replayed in sync with the video, including Super Chats, membership messages, and custom channel emotes. See [Live chat replay](#live-chat-replay)
- **Playlists, notes, notifications, statistics** — the usual library comforts

### Auth & Operations

- **Two auth modes**: single-user (no identity provider at all, for a single user) or multi-user via **Authentik** (OIDC) with **OpenFGA** fine-grained authorization
- **Secrets in OpenBao**:  storage credentials and cookies never sit in config files

---

# Getting Started

### Prerequisites
- Docker or Podman (infrastructure containers)

When using Aspire, you also need these prerequesites:
- Node.js 20+
- [pnpm](https://pnpm.io/)
- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [.NET 10 Runtime](https://dotnet.microsoft.com/)
- [.NET 10 ASP.NET Core](https://dotnet.microsoft.com/)
- [Aspire CLI](https://learn.microsoft.com/dotnet/aspire/) (optional — `dotnet run` on the AppHost works too)


---  

### Docker Compose

The ready-to-run Compose deployment is in `src/App/docker-compose-artifacts`:

```bash
cd src/App/docker-compose-artifacts
docker compose up -d --build
```

Review `.env` before exposing a deployment beyond your machine. It contains deployment-specific
credentials, public URLs, and service settings. The default frontend URL is
<http://localhost:25000>.

The Compose files are generated. Regenerate them after changing AppHost configuration:
The env is regenerated on generateCompose.sh, so be careful.
```bash
cd src/App
bash generateCompose.sh
(or generateCompose.ps1 on windows)
```


### Run for development (Aspire)

```bash
dotnet run --project src/App/AppHost/AppHost.csproj
```

AppHost loads [`aspire-development.env`](src/App/AppHost/aspire-development.env). The defaults are
for local development only. In multi-user mode, the development Authentik account is
`admin@localhost` / `froststream-dev-admin`.

For a minimal local setup, set the following before starting AppHost:

```env
SINGLE_USER_MODE="true"
```


### Debugging with Visual Studio
Visual Studio 2026/.NET 10 can break on async exceptions that leave user code and are caught by framework code.
  Cleipnir’s durable-suspension design matches that pattern exactly, so the debugger can be technically accurate by breaking/pausing exectuion but
  not helpful when trying to debug/run the application.

For each developer, configure this once for the solution:

  1. Debug → Windows → Exception Settings
  2. Under Common Language Runtime Exceptions, add:
      - Cleipnir.ResilientFunctions.Domain.Exceptions.Commands.SuspendInvocationException
      - Cleipnir.ResilientFunctions.Domain.Exceptions.Commands.PostponeInvocationException
      - optionally Cleipnir.ResilientFunctions.Domain.Exceptions.InvocationSuspendedException
  3. Ensure Break when thrown is unchecked.
  4. Right-click the exception row, enable the Additional Actions column if needed, then choose Continue When Unhandled
     in User Code.

This should help Visual Studio to not pause during the normal flow.

Everything — services, containers, config — is orchestrated by the AppHost:


## Configuration and data

- `src/App/AppHost/aspire-development.env` is the source of truth for local development settings.
- `FROSTSTREAM_STORAGE_ROOT` controls the shared host media directory; the default is `<repo>/data`.
- `FROSTSTREAM_BACKUP_ROOT` controls the Compose backup directory; its default is `./backups` beside the Compose file.
- `FROSTSTREAM_OPENBAO_BOOTSTRAP_ROOT` controls where OpenBao recovery material is stored. For Compose, it defaults to the ignored `./openbao-bootstrap` directory.

The generated OpenBao `init.env` contains an unseal key and initial root token. Keep it out of source
control and copy it to encrypted, off-host backup storage. See the backup guide before relying on a
deployment for important data.
THIS KEY IS NOT BACKED UP WITH THE STANDARD BACKUPS


---

## Architecture

```
                        ┌───────────────┐
   Browser ────────────▶│   Frontend    │  SvelteKit + Tailwind (BFF auth)
                        └──────┬────────┘
                               ▼
                        ┌───────────────┐     ┌────────────┐  ┌─────────┐
   Chromecast ◀────────▶│    WebAPI     │◀───▶│  Authentik │  │ OpenFGA │
                        └──────┬────────┘     └────────────┘  └─────────┘
                               │  NATS (request/reply + JetStream)
              ┌────────────────┼──────────────────┐
              ▼                ▼                  ▼
       ┌────────────┐   ┌────────────┐     ┌────────────┐
       │   Worker   │   │ DataBridge │     │ Scheduler  │
       │  (yt-dlp,  │   │ (EF Core,  │     │ (Quartz)   │
       │  storage)  │   │  sagas)    │     └────────────┘
       └────────────┘   └─────┬──────┘
                              ▼
                  PostgreSQL · Typesense · OpenBao · ClickHouse
```

#

## Key services

| Service | Responsibility |
| --- | --- |
| Frontend | SvelteKit browser UI and media playback experience. |
| WebAPI | HTTP API, browser auth/BFF, media streaming, and administration endpoints. |
| DataBridge | Durable metadata, workflow coordination, storage configuration, and live-chat ingestion. |
| Worker | Downloads media, materializes cookies, and writes media artifacts to configured storage. |
| MediaProcessor | Processes downloaded media and derived artifacts. |
| Scheduler | Runs recurring maintenance, reindexing, and backup work. |
| BackupService | pgBackRest backups and the standalone restore console. |

### Port scheme

All host ports live in one registry ([`src/App/AppHost/Ports.cs`](src/App/AppHost/Ports.cs)) and follow a two-range convention — the same numbers apply in development and in the compose deployment:

| Range                | Meaning                                                                            | Ports                                                                                                                                                                                      |
| -------------------- | ---------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| **25xy0** (external) | Host-published; browser/host-facing                                                | frontend `25000` · authentik `25100` · webapi `25200` (https `25210`) · scheduler `25300` · openbao `25400` · postgres `25500` · dbgate `25600` · nats-ui `25700` · openfga-studio `25800` · restore-console `25900` |
| **240xy** (internal) | Container-to-container only; bound on localhost in dev, never published by compose | typesense `24010` · pot-provider `24020` · openfga `24030` · nats `24040`–`24042` · backupservice `24050` · clickhouse `24060`                                                            |

External ports are overridable via `PORT_*` variables in the generated Aspire dev env.


### Live chat replay

Live chat replay is **opt-in**: it adds a ClickHouse container, which most deployments do not need. Chat volumes are large enough (a long stream can produce well over a million messages) that PostgreSQL is the wrong store for them.

Turn it on in `src/App/AppHost/aspire-development.env`:

```bash
LIVE_CHAT_ENABLED="true"
CLICKHOUSE_PASSWORD="…"   # required to be strong when FROSTSTREAM_PRODUCTION=true
```

Then restart `aspire run`, or regenerate the compose artifacts and `docker compose up -d`.
Because sidecars are archived regardless, enabling ClickHouse later can recover history for everything already in the library:

```bash
curl -X POST 'http://localhost:25200/api/media/watch/chat/backfill'
```

That queues a sweep of archived live streams with no ingested chat. Add `?mediaGuid=<guid>` for a single video, or `?force=true` to re-ingest ones that already have chat.

---

## Repository Layout

```
├── src/
│   ├── App/
│   │   ├── AppHost/                  # Aspire orchestrator + all env/port/secret wiring
│   │   ├── WebAPI/                   # REST API
│   │   ├── Worker/                   # yt-dlp + storage execution
│   │   ├── DataBridge/               # persistence, sagas, migrations
│   │   ├── Scheduler/                # Quartz jobs
│   │   ├── Frontend/                 # SvelteKit app
│   │   ├── BackupService/            # pgBackRest engine + OpenBao export + restore wizard
│   │   ├── Shared/                   # shared contracts & options
│   │   ├── StorageExtensions/        # Extensions for FluentStorage, mainly NFS/SMB/CIFS
│   │   └── docker-compose-artifacts/ # generated compose deployment
│   └── Libs/                         # reusable libraries (Conduit.NATS, …)
├── Tests/                            # unit tests
├── docs/                             # design notes & feature inventory
└── data/                             # default dev storage root (gitignored)
```

## Status

FrostStream is under active development. The core download → ingest → library → playback loop, creator subscriptions, bulk import, search, casting, auth, and backups are implemented; transcoding (MediaProcessor) is not yet built. Expect rough edges.


## Documentation

- [Running services](docs/Markdown/RUNNING_SERVICES.md)
- [Backup and restore](docs/Markdown/BACKUP_RESTORE.md)
- [Database migrations](docs/Markdown/MIGRATIONS.md)
- [Scaling guide](docs/Markdown/SCALING_GUIDE.md)
- [Proof-of-origin token provider](docs/Markdown/POT_PROVIDER.md)
- [OpenBao storage credentials](docs/secrets/openbao-storage-credentials.md)

## Development checks

Run the appropriate checks for the code you modify. For the frontend:

```bash
cd src/App/Frontend
pnpm run check
```

For the AppHost and backend projects:

```bash
dotnet build src/App/AppHost/AppHost.csproj
```

## Security note

The committed development settings intentionally include convenient local defaults. Replace every
credential, token, public origin, and bootstrap secret before a production deployment. Production
OpenBao should use multiple unseal shares or an external auto-unseal provider, and application
credentials should be narrowed from the development root-level policy.
