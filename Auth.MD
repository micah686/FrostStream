FrostStream Authentication and Authorization Architecture

FrostStream will use Authentik for authentication and OpenFGA for application authorization.

Authentik’s role

Authentik is responsible for identity-related concerns:

User login
Passwords
MFA
Account recovery
User profiles
Groups
OIDC/OAuth2 token issuance

FrostStream will not use ASP.NET Core Identity as its main user/password system. Instead, users authenticate through Authentik, and Authentik issues OIDC tokens that identify the user.

The FrostStream WebUI will redirect users to Authentik for login. The login page is owned by Authentik, not by the C# backend. FrostStream may customize Authentik’s branding and login flow, but Authentik remains the identity provider.

ASP.NET Core API’s role

The ASP.NET Core API is still responsible for validating incoming requests.

When a request comes into the FrostStream API, the API validates the Authentik-issued access token using standard ASP.NET Core JWT/OIDC middleware. The API does not usually need an Authentik-specific SDK.

The API validates things like:

Token signature
Token issuer
Token audience
Token expiration
Required claims

After validation, ASP.NET Core creates a ClaimsPrincipal for the request. FrostStream can then read claims such as:

sub — the Authentik user ID
email
preferred_username
groups

ASP.NET Core is used as the authentication enforcement layer, but not as the full application permission system.

OpenFGA’s role

OpenFGA is responsible for FrostStream-specific authorization.

Authentik answers:

Who is this user?

OpenFGA answers:

Is this user allowed to do this specific action on this specific FrostStream resource?

Examples:

Can user:abc view library:movies?
Can user:abc scan library:movies?
Can group:admins manage library:movies?
Can user:abc edit collection:kids-movies?
Can user:abc delete media:item-123?

This keeps FrostStream from scattering permission logic throughout controllers, services, and UI code.

Instead of writing many custom ASP.NET authorization handlers, FrostStream will have a thin authorization adapter that asks OpenFGA for access decisions.

Request flow
User
  -> FrostStream WebUI
  -> Authentik login
  -> Authentik issues token
  -> WebUI calls FrostStream API
  -> ASP.NET Core validates token
  -> FrostStream extracts user subject/groups
  -> FrostStream asks OpenFGA for permission decision
  -> API allows or denies the request
Example permission checks

A FrostStream endpoint might receive a request like:

POST /api/libraries/movies/scan

The API first confirms that the request is authenticated.

Then FrostStream asks OpenFGA:

Can user:abc scan library:movies?

If OpenFGA returns yes, the scan starts.

If OpenFGA returns no, the API returns 403 Forbidden.

Another example:

GET /api/libraries/movies/items

FrostStream asks:

Can user:abc view library:movies?

Another example:

POST /api/libraries/movies/settings

FrostStream asks:

Can user:abc manage library:movies?
Group-based access

Authentik can provide group membership, such as:

group:admins
group:family
group:editors

FrostStream can map those groups into OpenFGA relationships.

For example:

group:admins can manage library:movies
group:family can view library:movies
group:editors can scan library:movies

Then OpenFGA can answer questions like:

Can group:admins manage library:movies?
Can user:alice manage library:movies because she is a member of group:admins?
Can user:bob view library:movies because he is a member of group:family?
Local FrostStream data

FrostStream may still keep a local user/profile table, but not for passwords.

Example:

FrostStreamUser
- Id
- AuthentikSubjectId
- DisplayName
- LastSeenAt
- Preferences

Authentik owns identity. FrostStream owns app-specific data.

Summary

The final architecture is:

Authentik
  Authentication, login, users, groups, MFA, tokens

ASP.NET Core API
  Token validation, request authentication, endpoint enforcement

OpenFGA
  Fine-grained FrostStream permissions

FrostStream database
  App data, user preferences, media libraries, local resource metadata

This avoids building a custom identity system in C#, avoids overloading ASP.NET Core authorization with complex app-specific rules, and keeps FrostStream’s permission model centralized and testable.
