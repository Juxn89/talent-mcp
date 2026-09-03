# ADR-0005 · Client registration: pre-registration, not DCR or CIMD

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 3 Sep 2026 |
| **Phase** | F3 (OAuth 2.1 with Keycloak) |
| **Supersedes** | — |

## Context

The 2026-07-28 revision recognizes three client registration mechanisms and gives clients a priority
order ([Client Registration](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration)):

1. **Pre-registration** — a client uses a client id it already has, "such as those supplied by a
   pre-registration flow," including hard-coding one.
2. **Client ID Metadata Documents (CIMD)** — the client's id *is* an HTTPS URL it hosts; the
   authorization server fetches that URL for `client_name`/`redirect_uris`/etc. at authorization time.
   Advertised by the authorization server via `client_id_metadata_document_supported` in its metadata.
3. **Dynamic Client Registration (DCR, RFC 7591)** — explicitly marked deprecated on the spec page:
   > "Dynamic Client Registration is deprecated. New implementations should use Client ID Metadata
   > Documents instead. This option remains available for backwards compatibility with authorization
   > servers that do not support Client ID Metadata Documents."

AGENTS.md already recorded the direction implied by that warning — DCR is on the "do not use" table,
CIMD is listed as the replacement — but flagged that the choice needed an ADR once F3 actually wired
OAuth. This is that ADR, and it does not land where AGENTS.md's one-line note assumed it would.

**What CIMD requires of the authorization server**, per the same spec section: it must fetch the
metadata document, validate `client_id` matches the URL exactly, validate the redirect URIs against
it, and advertise `client_id_metadata_document_supported: true`. That is server-side work Keycloak has
to implement, not something this project's resource-server code can add on its own.

**Keycloak's CIMD support, verified 3 Sep 2026:**

- Introduced in **Keycloak 26.6.0** (April 2026), behind the feature flag `--features=cimd`. The
  official guide ([keycloak.org/securing-apps/mcp-authz-server](https://www.keycloak.org/securing-apps/mcp-authz-server),
  referencing nightly **26.7.3**) states outright: *"The OAuth Client ID Metadata Document support in
  Keycloak is an experimental feature. It may introduce breaking changes in future versions of
  Keycloak."*
- A currently open defect, [keycloak/keycloak#49730](https://github.com/keycloak/keycloak/issues/49730):
  with `--features=cimd` enabled, the discovery document advertises
  `client_id_metadata_document_supported: true` but omits `"none"` from
  `token_endpoint_auth_methods_supported` — and an MCP public client's token exchange needs both. As
  filed, this makes CIMD **unusable** for exactly the kind of client this project's demo/E2E client is.
- The roadmap targets Keycloak **26.8.0** (end of September 2026) to promote CIMD from experimental to
  preview. `deploy/compose.yaml` pins `26.7.2` — a patch behind even the nightly the guide was written
  against, and two minor releases behind that roadmap target.

This project's compose stack (`deploy/keycloak/realm.json`) already, since F0, statically declares
both OAuth clients — `talent-mcp-server` (the resource server, all grant types disabled) and
`talent-mcp-client` (public, PKCE S256) — with fixed `clientId` values. That is pre-registration,
option 1 above, not a placeholder for something else.

## Decision

**Use pre-registration for both clients. Do not implement DCR. Do not enable Keycloak's CIMD feature
flag for this project.**

Concretely:

- `deploy/keycloak/realm.json` keeps declaring `talent-mcp-server` and `talent-mcp-client` as static
  clients, as it already does.
- The demo/E2E client sets `ClientOAuthOptions.ClientId = "talent-mcp-client"` explicitly. Per the
  SDK's own doc comment on that property ("If not provided, the client will attempt to register
  dynamically"), setting it is what prevents the SDK from ever entering the DCR path.
  `ClientOAuthOptions.ClientMetadataDocumentUri` is left unset, so the CIMD path
  (`AuthorizationServerMetadata.ClientIdMetadataDocumentSupported`) is never exercised either — Keycloak
  would report that flag `false` here regardless, since `deploy/compose.yaml` does not pass
  `--features=cimd`.
- The realm does **not** enable `--features=cimd`.

This ranks first in the spec's own priority order, so it is not a workaround dressed up as a choice —
it is the option the spec itself puts ahead of CIMD when a pre-existing relationship exists between
client and server, which is exactly this project's shape (one demo client, one realm, both defined in
the same versioned file).

## Alternatives considered

**Client ID Metadata Documents.** This is the spec's designated DCR replacement and the more
forward-looking thing to demonstrate — declined only because of where the *implementation* stands, not
the *specification*. Depending on an experimental Keycloak feature with a filed, open, and (per the
issue) blocking bug for MCP-shaped clients would mean the F3 OAuth work either doesn't run at all, or
runs against behavior Keycloak's own docs say may break in the next release — neither is compatible
with this project's "verify before applying, verified running" standard (Principle #6). Revisit when
either lands: Keycloak's CIMD support reaching at least preview status (tracked for 26.8.0), or
keycloak/keycloak#49730 closing — whichever comes first, re-verify against the then-current image tag
before switching.

**Dynamic Client Registration.** Rejected outright, independent of Keycloak's CIMD readiness: the spec
deprecates it, this project has no backwards-compatibility obligation to a pre-2026-07-28 authorization
server, and `talent-mcp-server`'s realm client already has every grant type disabled — it exists to be
a token audience, not to register itself or anything else.

## Consequences

**`ClientOAuthProvider.PerformDynamicClientRegistrationAsync` and the CIMD resolution path are dead
code paths for this project's own client**, by construction — not a gap the E2E suite failed to
cover. A conformance or E2E assertion that Keycloak's metadata reports
`client_id_metadata_document_supported: false` documents this decision is in effect; asserting `true`
or driving a real CIMD exchange would be asserting a feature this compose stack does not turn on.

**`Authorization Server Binding`** — the spec's requirement that a client index its stored credentials
by the issuing authorization server's `issuer` — still applies to the demo/E2E client's own credential
handling even though CIMD is unused: pre-registered credentials are exactly the kind the spec says
"are inherently specific to a particular authorization server" and must not be reused across issuers.
This is unaffected by this ADR and is implemented where the demo/E2E client stores its token.

**If A1 or a future consumer needs to onboard third-party MCP clients this project has no pre-existing
relationship with**, pre-registration stops fitting — that is precisely the scenario CIMD exists for.
This ADR's decision is scoped to *this* project's one demo client against *its own* realm, not a claim
that pre-registration is the right answer generally.

## Verification

- Spec page read directly: [Client Registration — 2026-07-28](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization/client-registration),
  3 Sep 2026 — priority order, the DCR deprecation warning, and the CIMD/pre-registration requirements
  above are quoted from it.
- SDK surface read from `ModelContextProtocol.Core.xml` @ 2.2.0: `ClientOAuthOptions.ClientId`,
  `.ClientMetadataDocumentUri`, `.DynamicClientRegistration`, `AuthorizationServerMetadata.ClientIdMetadataDocumentSupported`.
- Keycloak CIMD status verified 3 Sep 2026 against
  [keycloak.org/securing-apps/mcp-authz-server](https://www.keycloak.org/securing-apps/mcp-authz-server)
  (nightly 26.7.3) and [keycloak/keycloak#49730](https://github.com/keycloak/keycloak/issues/49730);
  `deploy/compose.yaml` pins `quay.io/keycloak/keycloak:26.7.2`.
