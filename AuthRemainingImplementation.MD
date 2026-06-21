# FrostStream Auth Remaining Implementation

This writeup assumes the current backend foundation is in place:

- `SINGLE_USER_MODE=true` injects the synthetic owner user and skips Authentik/OpenFGA.
- Multi-user mode validates Authentik JWTs in WebAPI.
- WebAPI calls OpenFGA for a coarse system-level permission check.
- AppHost starts Authentik, Authentik worker, Redis, OpenFGA, and OpenFGA migration against the existing `froststreamdb` Postgres resource.
- DataBridge has the local `auth.froststream_users` table and seeds the stable single-user owner row.
- SvelteKit has basic BFF routes for login/callback/logout and an `/api/*` proxy that stores tokens in httpOnly cookies.

## 1. Authentik Setup

Create and configure the Authentik application/provider that matches the AppHost/WebAPI settings.

Required Authentik objects:

- OIDC provider for FrostStream.
- Application that uses that provider.
- Client ID, default `froststream-bff`.
- Client secret, provided to frontend via `AUTHENTIK_CLIENT_SECRET`.
- Redirect URI: `https://<frontend-host>/auth/callback` for deployed environments.
- Logout redirect URI: `https://<frontend-host>/`.
- Audience/API identifier: `froststream-api`.
- Scopes/claims: `openid profile email groups`.
- Group claim must emit usable group names, especially `owner`, `admins`, `users`, and `viewers` or whatever final role names you choose.

Environment/config values to set:

- `AUTHENTIK_AUTHORITY`
- `AUTHENTIK_CLIENT_ID`
- `AUTHENTIK_CLIENT_SECRET`
- `AUTHENTIK_API_AUDIENCE`
- `AUTHENTIK_BOOTSTRAP_EMAIL`
- `AUTHENTIK_BOOTSTRAP_PASSWORD`
- `AUTHENTIK_SECRET_KEY`

Acceptance criteria:

- Browser login redirects to Authentik.
- Callback exchanges the code server-side.
- Browser storage contains no access token.
- API receives a Bearer token only from the SvelteKit server proxy.
- WebAPI rejects no-token and invalid-token requests in multi-user mode.

## 2. OpenFGA Store And Model

Create an OpenFGA store and authorization model, then configure WebAPI with the IDs.

Environment/config values to set:

- `OPENFGA_STORE_ID`
- `OPENFGA_AUTHORIZATION_MODEL_ID`
- `OpenFga__ApiToken` if OpenFGA auth is enabled later.

Initial coarse model should allow the current WebAPI fallback check:

```fga
model
  schema 1.1

type user

type group
  relations
    define member: [user]

type system
  relations
    define owner: [user, group#member]
    define admin: [user, group#member] or owner
    define member: [user, group#member] or admin
    define viewer: [user, group#member] or member
    define access: viewer
    define manage: admin
```

Required bootstrap tuples:

```text
system:froststream#owner@user:<your-authentik-sub>
```

or group-based:

```text
system:froststream#admin@group:admins#member
group:admins#member@user:<your-authentik-sub>
```

Important current behavior:

- WebAPI currently asks OpenFGA for `user:<sub> access system:froststream`.
- If the store/model/tuples are missing, multi-user API calls will be denied.

Acceptance criteria:

- A known admin user gets 200 through the API.
- A valid but unprivileged user gets 403.
- Missing/invalid token gets 401.

## 3. Group Synchronization

Implement the bridge from Authentik groups to OpenFGA tuples.

Recommended path:

- On successful BFF login, inspect the ID/access token group claims server-side.
- Upsert the local FrostStream user by Authentik `sub`.
- Write or refresh OpenFGA tuples:
  - `group:<name>#member@user:<sub>`
  - Optional system role tuples, such as `system:froststream#admin@group:admins#member`.

Implementation options:

- SvelteKit calls a protected WebAPI endpoint after login, such as `POST /api/auth/session`.
- WebAPI validates the token, upserts the local user through DataBridge, and syncs OpenFGA tuples.
- Alternatively, a DataBridge consumer can own local user persistence once WebAPI publishes a user-seen message.

Acceptance criteria:

- First login creates or updates `auth.froststream_users`.
- Group changes in Authentik are reflected in OpenFGA after the next login/session refresh.
- Removed group membership no longer grants access.

## 4. Per-Resource Authorization

Replace the current coarse fallback check with endpoint/resource-specific checks.

Current coarse check:

```text
user:<sub> access system:froststream
```

Needed resource checks:

- Downloads:
  - create download: `download:create` or `manage system:froststream`
  - view own download: `view download:<id>`
  - admin view all downloads: `manage system:froststream`
- Playlists:
  - create/list own playlists: `manage user:<sub>` or `owner playlist:<id>`
  - shared playlist view/edit relations later.
- Cookie profiles:
  - create/list/check/delete only owner profiles.
  - admins may see metadata only if intentionally allowed.
  - nobody can read cookie content back.
- Storage:
  - storage config changes should require admin/manage.
- Schedules and creator sources:
  - create/update/delete should require admin/manage.
  - list/read may be member/viewer depending on final policy.
- Metadata/media streaming:
  - view should require viewer/member access.
  - future per-library/media restrictions can be layered later.

Acceptance criteria:

- Every controller endpoint has an explicit auth policy or a documented anonymous exception.
- Cross-user cookie profile access is denied.
- Admin endpoints deny regular users.
- Tests cover 401, 403, and permitted paths.

## 5. Identity Propagation Over NATS

Formalize user identity in message contracts instead of relying on free-text fields like `RequestedBy`.

Needed changes:

- Add `UserId` / `SubjectId` to commands that create user-owned resources.
- Preserve `RequestedBy` only as display/audit text if useful.
- Carry identity through:
  - download requests
  - playlist requests
  - cookie profile operations
  - creator source changes
  - schedule changes
  - storage config changes

Data model changes likely needed:

- owner/user columns on playlists.
- owner/user columns on cookie profile metadata.
- owner/user columns on download jobs if users should see only their own jobs.
- possibly audit columns: `created_by`, `last_modified_by`.

Acceptance criteria:

- DataBridge can persist ownership without trusting WebAPI free-text.
- Worker receives only opaque references for secrets and never receives cookie body contents.
- User-scoped resources can be filtered and authorized consistently.

## 6. Cookie Profile Ownership

Finish the ownership model from `OpenBao Cookie Secrets Plan.md`.

Needed changes:

- Store cookie secrets under stable user-scoped paths:
  - `cookies/users/{userId}/{profileKey}`
- Persist only non-secret metadata in Postgres.
- Update cookie APIs to use the authenticated user by default.
- Ensure admins cannot retrieve cookie bodies.
- Update downloads to reference a user-owned cookie profile, not a global cookie key.

Acceptance criteria:

- User A cannot list/check/delete User B's profiles.
- Worker can materialize User A's selected profile for a download.
- Postgres contains no cookie content.
- Temp cookie files are deleted after use.

## 7. Frontend Completion

The BFF skeleton exists, but the UI still needs real auth state handling.

Needed changes:

- On startup, call `/api/auth/config`.
- In single-user mode, render normally without login.
- In multi-user mode:
  - show login/logout controls.
  - route unauthenticated users to `/auth/login`.
  - make all API calls through the SvelteKit `/api/*` proxy.
  - handle 401 by redirecting to login.
  - handle 403 with a useful permission-denied UI.
- Add token refresh before access-token expiry.
- Optionally call IdP end-session endpoint during logout.

Acceptance criteria:

- Browser never stores tokens in local/session storage.
- Refresh works without user interaction.
- Logout clears local session and optionally ends the Authentik session.

## 8. Production Hardening

Before exposing FrostStream beyond a trusted LAN/local environment:

- Set `SINGLE_USER_MODE=false`.
- Set strong `AUTHENTIK_SECRET_KEY`, `AUTHENTIK_CLIENT_SECRET`, and bootstrap password.
- Do not rely on generated AppHost secrets in production.
- Use HTTPS for Authentik, frontend, and WebAPI.
- Set `Auth__RequireHttpsMetadata=true`.
- Keep OpenAPI/Scalar disabled unless intentionally exposed.
- Pin container versions instead of `latest`.
- Back up the existing Postgres database, including:
  - FrostStream schemas.
  - Authentik tables.
  - OpenFGA tables.
  - Cleipnir tables.
- Decide whether Authentik/OpenFGA should share `froststreamdb` long term or use separate databases on the same Postgres server.

Acceptance criteria:

- Production startup fails or loudly warns if single-user mode is enabled.
- OpenAPI is not public by default.
- All auth secrets come from deployment secrets, not checked-in config.

## 9. Tests To Add

Backend:

- Single-user mode authenticates every request and uses `AllowAllAuthorizer`.
- Multi-user mode rejects anonymous requests.
- Multi-user mode returns 403 when OpenFGA denies.
- Multi-user mode returns 200 when OpenFGA allows.
- `/api/auth/config` is anonymous in both modes.
- Health endpoints remain anonymous.

OpenFGA:

- Check request maps to `user:<sub>`, relation, and object correctly.
- Missing OpenFGA config denies in multi-user mode.

DataBridge:

- Single-user owner seeder creates the stable owner row.
- Seeder updates the same row instead of creating duplicates.

Frontend:

- Login creates state/verifier cookies and redirects to Authentik.
- Callback rejects invalid state.
- Callback stores token response in an httpOnly cookie.
- Proxy attaches Bearer in multi-user mode.
- Proxy does not require token for `/api/auth/config`.

## 10. Suggested Implementation Order

1. Configure Authentik OIDC app and confirm WebAPI token validation.
2. Create OpenFGA store/model and bootstrap admin tuple.
3. Add an auth/session sync endpoint to upsert local users and OpenFGA group tuples.
4. Add per-resource policies to the highest-risk endpoints first: cookies, storage, downloads, schedules.
5. Add ownership columns and identity propagation over NATS.
6. Finish frontend login/logout/session refresh behavior.
7. Add full 401/403/allowed-path coverage.
8. Harden production deployment settings.
