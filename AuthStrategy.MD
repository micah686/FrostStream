# FrostStream Auth Strategy (AAA)

> **Status:** Strategy / design record. No source code is changed by this document — it captures the chosen Authentication & Authorization architecture and the intended implementation path so it can be reviewed before any code is written.

## Context

FrostStream has **zero authentication/authorization today**. WebAPI registers `AddAuthorization()`/`UseAuthorization()` (`src/App/WebAPI/Program.cs:19,61`) but no authentication scheme, no policies, and no `[Authorize]` anywhere — the authz middleware is inert and every endpoint is anonymous. OpenAPI/Scalar is exposed unconditionally. The FABLE evaluation (`FABLE_REPORT.MD`) rates Security 2/10 and calls auth "unstarted" — the single biggest risk for any non-localhost deployment.

The goal is a real AAA design (focus: Authentication + Authorization) that supports multiple users with RBAC, **while keeping the common case — a single self-hoster, or local testing — frictionless** via a `SINGLE_USER_MODE` short-circuit that grants full access with no IdP and no access-control friction.

## Decisions

| Area | Decision |
|---|---|
| Identity provider | **Authentik** (OIDC) — owns login, passwords, MFA, recovery, groups, token issuance |
| Frontend token handling | **BFF / httpOnly cookies** — SvelteKit server is the confidential OIDC client and token holder; browser never sees a token |
| Authorization engine | **OpenFGA, roles-first** — small model now (owner/admin/user/viewer from Authentik groups), per-resource/owner tuples added incrementally |
| Single-user backdoor | **Synthetic owner + skip IdP** — inject a fixed authenticated owner principal, allow-all authz, and do not start Authentik/OpenFGA |

## Target architecture

Four responsibilities, cleanly separated (consistent with `Auth.MD`):

- **Authentik** — *Authentication.* Who is this user? Login UI, passwords, MFA, account recovery, groups, OIDC token issuance.
- **SvelteKit server (BFF)** — confidential OIDC client. Runs the authorization-code + PKCE flow against Authentik, exchanges the code server-side, and stores tokens in an **httpOnly, Secure, SameSite session cookie** (or a server session keyed by an opaque cookie id). It proxies browser calls to the API, attaching the access token as a `Bearer` header server-to-server. The browser holds only the session cookie.
- **ASP.NET Core API (resource server)** — *Authentication enforcement.* Validates the Authentik-issued JWT (signature, issuer, audience, expiry, required claims) via `AddAuthentication().AddJwtBearer(...)` + `UseAuthentication()`. Builds a `ClaimsPrincipal` (sub, email, preferred_username, groups). Calls OpenFGA for each protected action.
- **OpenFGA** — *Authorization.* "Can `user:<sub>` `<relation>` `<object>`?" Fine-grained, centralized, testable; keeps permission logic out of controllers.

### Request flow — multi-user (default)
```
Browser -> SvelteKit (BFF) --(no session)--> redirect to Authentik login
Authentik -> issues code -> SvelteKit server exchanges code (PKCE) -> stores tokens in httpOnly cookie
Browser -> SvelteKit (session cookie) -> SvelteKit attaches Bearer -> ASP.NET API
ASP.NET API -> validate JWT -> extract sub/groups -> ask OpenFGA(Check) -> 200 | 403
```

### Request flow — single-user mode
```
Browser -> SvelteKit (no login redirect; treats user as owner) -> ASP.NET API
ASP.NET API -> SingleUser auth handler injects fixed owner principal -> AllowAll authorizer -> 200
(Authentik + OpenFGA are not running)
```

## SINGLE_USER_MODE design (the unique requirement)

A single env var `SINGLE_USER_MODE` (truthy = on) flips the whole system between the two flows. **Read once at startup**, never per-request, so the security boundary is a single explicit branch.

**WebAPI (`src/App/WebAPI/Program.cs`)** — branch the registration:
- **On:** register a custom `SingleUserAuthenticationHandler` (scheme set as default) that authenticates *every* request as a fixed `ClaimsPrincipal`:
  - `sub` = a **stable constant owner id** (e.g. `single-user-owner`) — critical because OpenBao cookie profiles are user-scoped (`cookies/users/{userId}/...` per `OpenBao Cookie Secrets Plan.md`); the id must persist across restarts.
  - `email`, `preferred_username` = sensible owner defaults; `groups` = `["owner","admins"]`.
  - Register an `AllowAllAuthorizer : IFrostStreamAuthorizer` that returns "permitted" for every check — no OpenFGA call.
- **Off:** register `AddJwtBearer` (authority = Authentik issuer, audience = `froststream-api`) and the `OpenFgaAuthorizer`.
- `UseAuthentication()`/`UseAuthorization()` stay in the pipeline in both modes so behavior is identical in shape; only the handler/authorizer differ.

**AppHost (`src/App/AppHost/AppHost.cs`)** — read `SINGLE_USER_MODE` via `Environment.GetEnvironmentVariable` (same pattern as `FROSTSTREAM_STORAGE_ROOT`/`JWT_SECRET`):
- **On:** do **not** add Authentik (+ its Postgres DB + Redis) or OpenFGA (+ its Postgres DB) resources. Pass `SINGLE_USER_MODE=true` to `webapi` and `frontend`.
- **Off:** add the Authentik and OpenFGA container stacks (see below) and inject their endpoints/issuer into `webapi`.

**Frontend (SvelteKit)** — receive the flag (`VITE_`/server env). When on, skip the Authentik redirect entirely and render as the owner; when off, run the BFF login flow. The flag is also surfaced by an **unauthenticated** `GET /api/auth/config` endpoint (returns `{ mode: "single-user" | "multi-user", authority, ... }`) so the UI has one source of truth.

**Guardrails (included regardless of mode):**
- Emit a **loud startup warning** log when single-user mode is active ("AUTH DISABLED — full access granted to all requests").
- Reflect the mode in `/api/auth/config` and ideally in OpenAPI/health metadata.
- Gate the currently-unconditional OpenAPI/Scalar exposure behind environment/config.
- **Optional safety interlock (recommended):** refuse to start, or require an explicit `SINGLE_USER_MODE_CONFIRM` second flag, if single-user mode is combined with a production marker (`ASPNETCORE_ENVIRONMENT=Production`). Default: warn-only; interlock is a config toggle.

## Component changes (intended implementation)

1. **Shared authz adapter** (`src/App/Shared/`, new `Auth/` area): `IFrostStreamAuthorizer` with `CheckAsync(userId, relation, objectRef)`; implementations `OpenFgaAuthorizer` and `AllowAllAuthorizer`. Expose a thin resource-based ASP.NET integration: a `PermissionRequirement(relation, resourceType)` + `AuthorizationHandler` that calls `IFrostStreamAuthorizer`, so controllers use `[Authorize]` + a resource check rather than bespoke logic.
2. **WebAPI**: add `AddAuthentication`/`AddJwtBearer` (off-mode) and the single-user handler (on-mode); register the authorizer; add `/api/auth/config`; protect controllers under `Features/*` with the resource-based policy. Today's open routes: `/api/Playlists`, `/api/Downloads`, `/api/Cookies`, `/api/Storage`, `/api/option-presets`, `/api/schedules`, `/api/creator-sources`, `/api/metadata`, `/stream`.
3. **Frontend (BFF)**: add SvelteKit server hooks for the OIDC code+PKCE flow, session-cookie handling, an API proxy that attaches the bearer, and login/logout routes. Cookie signing/encryption keys can be sourced from OpenBao for multi-instance consistency.
4. **Local user table** (DataBridge, new FluentMigrator `026_`): `FrostStreamUser { Id, AuthentikSubjectId, DisplayName, LastSeenAt, Preferences }` per `Auth.MD` — app data only, no passwords. In single-user mode, ensure the synthetic owner row exists (stable id).
5. **Identity propagation over NATS**: carry the authenticated `userId` on commands so DataBridge/Worker can scope per-user resources (notably cookie profiles read from OpenBao by the Worker). Formalize the existing free-text `RequestedBy` (`PlaylistsController.cs:55`) into a real `UserId` on message contracts / headers.
6. **AppHost container stacks** (off-mode only): Authentik (server + worker + its Postgres DB via `postgres.AddDatabase(...)` + Redis) and OpenFGA (+ its Postgres DB), following the existing `AddContainer(...).WithEnvironment(...).WithHttpEndpoint(...)` + `.WaitFor`/`.WithReference` pattern used for `typesense`/`openbao`. Inject Authentik issuer/discovery URL and OpenFGA endpoint into `webapi` via the `__` config convention.

## OpenFGA initial model (roles-first)

Start minimal; grow toward ReBAC:
- Types: `user`, `group`, and a global `system` object with relations `owner`, `admin`, `member` (user), `viewer`.
- Map Authentik groups → OpenFGA tuples at login/sync (e.g. `group:admins#member`, `system:froststream#admin`).
- First per-resource step: ownership of user-scoped resources, especially **cookie profiles** (and downloads/playlists) so a user can manage only their own — directly supports the `OpenBao Cookie Secrets Plan.md` requirement that users access only their own cookie profiles and admins never see contents.
- Each protected endpoint maps to a `(relation, objectType)` check (e.g. scan → `manage`, list → `view`).

## Out of scope / future hardening

- **Service-to-service / NATS auth:** the bus is currently fully trusted with no per-message identity (`nats-server.conf` has no `accounts`/`users`). User identity propagation (item 5) is in scope; locking down the bus itself (NATS accounts/nkeys, TLS `AllowInsecureTransport=false`) is future work.
- **SSRF guard** on user-supplied yt-dlp URLs (flagged by FABLE) — related to authorization-of-actions but tracked separately.
- **Accounting** (the third A): audit logging of who-did-what is a natural follow-on once identity exists; noted but not designed here.

## Verification (once implemented)

- **Single-user mode:** start AppHost with `SINGLE_USER_MODE=true`; confirm Authentik/OpenFGA containers are absent, startup logs the warning, `/api/auth/config` returns `single-user`, every endpoint responds without a token, and a cookie profile created by the owner persists across a restart (stable id).
- **Multi-user mode:** start without the flag; confirm redirect to Authentik, successful BFF login sets an httpOnly cookie (no token in browser storage), API rejects requests with no/invalid JWT (401), returns 403 when OpenFGA denies, and 200 when permitted. Verify an Authentik `admins` group member can `manage` and a `family`/viewer member can only `view`.
- **Authz unit tests:** `OpenFgaAuthorizer` Check mapping; `AllowAllAuthorizer` always permits; cookie-profile ownership denies cross-user access (extends the test plan in `OpenBao Cookie Secrets Plan.md`).
- **Mode isolation:** assert that with the flag on, no JWT validation path executes and no OpenFGA call is made (handler/authorizer swapped at startup).
