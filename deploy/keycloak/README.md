# Keycloak realm — `realm.json`

Versioned OAuth 2.1 configuration for the `talent` realm, imported by `start-dev --import-realm`.

## Two traps, both hit for real on 27 Aug 2026

**1. No comment keys in `realm.json`.** Keycloak deserializes it with Jackson configured to reject
unknown fields, so a `"_comment"` key fails the import with `Unrecognized field "_comment" (class
org.keycloak.representations.idm.RealmRepresentation), not marked as ignorable` — and with
`restart: unless-stopped` the container **crash-loops** rather than reporting a clear failure. That
is why this file exists: the explanation lives here and the JSON stays a pure representation. Real
Keycloak fields such as `description` on a client are fine and are used where they help.

**2. Declaring `clientScopes` replaces Keycloak's built-in set — it does not add to it.** This one
is silent and much nastier. A first version of this realm declared only the four `talent.*` scopes,
which meant `acr`, `basic`, `email`, `profile`, `roles` and `web-origins` were **never created**.
The client's `defaultClientScopes` then resolved to nothing, and because `basic` carries the
`oidc-sub-mapper`, **every access token came out with no `sub` claim** — no error anywhere, the
realm imported cleanly and tokens minted fine. The plan rate-limits per OAuth `sub`, and a resource
server needs it, so this would have surfaced much later as a confusing bug.

So `realm.json` now contains all 15 built-in scopes plus the 4 `talent.*` ones, 19 in total.

### How `realm.json` was generated

Not hand-written, and not to be hand-edited scope-by-scope. The built-in definitions were read back
from Keycloak itself so they are exact rather than remembered:

1. Create a pristine realm via the admin API (`POST /admin/realms` with `{"realm":"scratch"}`) so
   Keycloak generates its own built-in client scopes.
2. `GET /admin/realms/scratch/client-scopes` plus `default-default-client-scopes` and
   `default-optional-client-scopes` for the authoritative assignment lists.
3. Strip the server-generated `id` fields, append the four `talent.*` scopes, add the clients and
   the seed user.
4. Delete the scratch realm.

Redo that if the Keycloak major version changes. F3 may instead switch to committing a full
`partial-export` of the configured realm — note that export omits users and client secrets, which
is why it was not used now.

## What F0 established

Verified against the running stack, not just written:

- The realm imports cleanly (0 error lines in the Keycloak log) and the container reaches `healthy`
  in ~55s.
- **Risk #3 is closed.** `code_challenge_methods_supported` is `["plain", "S256"]` — S256 is
  advertised, which is what the MCP SDK's OAuth support requires; it refuses an authorization server
  whose metadata omits it. `pkce.code.challenge.method: S256` is pinned on `talent-mcp-client`.
- Tokens carry `sub`, `preferred_username`, the correct `iss`, and `aud: talent-mcp-server` from the
  audience mapper.
- Scopes are granted **only when requested**: asking for `openid` alone yields no `talent.*` scope,
  which is what makes the F3 denial test meaningful.

> **Note on `plain`.** Keycloak advertises both `plain` and `S256` realm-wide and does not let you
> remove `plain` from the discovery document; enforcement is per-client. So the conformance test
> should assert that **S256 is present** (the SDK's actual requirement) and, separately, that
> `talent-mcp-client` **rejects a `plain` challenge**. Asserting `["S256"]` exactly would fail
> against a correctly configured Keycloak.

F3 completes the wiring: scope-to-tool enforcement inside the resource server, RFC 9207 `iss`
validation, credentials indexed by issuer, Client ID Metadata Documents instead of DCR, and the
conformance test that asserts S256 in the metadata.

## Clients

| Client | Kind | Why |
|---|---|---|
| `talent-mcp-server` | Confidential, **all grant types disabled** | The resource server. It never starts a flow; it is the token audience and the thing scopes are validated against. |
| `talent-mcp-client` | **Public + PKCE S256** | The demo / E2E client. This is the OAuth 2.1 shape the 2026-07-28 revision mandates. |

An `oidc-audience-mapper` on `talent-mcp-client` puts `talent-mcp-server` into the access token's
`aud`, which is what lets the resource server reject tokens minted for something else.

## Why the scopes are optional, not default

A client has to **ask** for a scope. That makes "a token without `talent.candidates.reject`" the
normal case rather than a special one, which is precisely what the E2E denial test needs to be
meaningful — with default scopes every token would carry everything and the test would prove
nothing.

## Dev-only credentials

Everything here is a local default, and none of it is a production secret:

| What | Value |
|---|---|
| Keycloak admin | `admin` / `admin` |
| Seed user | `recruiter` / `recruiter` |
| Resource server secret | `dev-only-resource-server-secret` |

`directAccessGrantsEnabled` on `talent-mcp-client` is **dev-only**, so E2E tests can mint a token
without driving a browser. It is not part of the flow the project demonstrates, and production
config must not enable it.

## Re-importing after an edit

`--import-realm` only imports when the realm is absent, so a plain restart will **not** pick up
changes to this file — that is deliberate, so a restart does not clobber edits made through the
admin console. To force a re-import, drop the volume:

```bash
docker compose -f deploy/compose.yaml down -v
docker compose -f deploy/compose.yaml up -d
```

## Verifying by hand

```bash
# S256 must appear here, or the SDK's OAuth support refuses the server
curl -s http://localhost:8080/realms/talent/.well-known/openid-configuration \
  | jq '.code_challenge_methods_supported, .issuer'

# Mint a token with one scope (dev-only direct grant)
curl -s -X POST http://localhost:8080/realms/talent/protocol/openid-connect/token \
  -d grant_type=password -d client_id=talent-mcp-client \
  -d username=recruiter -d password=recruiter \
  -d scope='openid talent.jobs.read' | jq -r .access_token
```
