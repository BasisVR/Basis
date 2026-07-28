# Basis SSO (OIDC) Integration

Client-side OpenID Connect single sign-on for the BasisVR **Desktop/PCVR** client, for
closed-org deployments. The client is gated at launch behind an OIDC login (Authorization
Code + PKCE, system browser, 127.0.0.1 loopback redirect). The session is persisted
encrypted-at-rest and the local DID identity is bound to the signed-in user. **The server /
DID network protocol is unchanged** — this is a client-side gate only.

Full design: `docs/sso-spec.md` at the repo root.

## Status

Implemented (this package):

- `BasisOidcConfig` — runtime JSON config loader (streamingAssets default → persistentData override).
- `BasisSsoSession` / `BasisSsoSessionStore` — session model + device-key-encrypted persistence.
- `ISsoTokenValidator` / `BasisRsaJwksTokenValidator` — id_token JWKS/RS256 signature + claim checks.
- `BasisSsoAccessControl` — optional allowed-groups / allowed-claims rules.
- `BasisOidcLoginService` — discovery, PKCE, loopback callback, token exchange + refresh, UserInfo merge.
- `BasisSsoIdentityBinding` — namespaces the DID keypair per `sub`, seeds/persists per-account display name.
- `BasisSsoAuthController` — orchestration brain used by the UI and the launch gate.

Pending (integration): the launch-gate MonoBehaviour + login screen UI, the Settings sign-out /
switch-account UI, and suppressing `BasisConnectionService` auto-connect until sign-in completes.

## Configuration

Ship `basis-sso.json` in `Assets/StreamingAssets/` (see `Samples~/basis-sso.sample.json`). A copy
in `Application.persistentDataPath` overrides it per machine. Schema is documented in the spec §5.

### Google (current target)

Create an OAuth client of type **Desktop app** in Google Cloud Console → *APIs & Services →
Credentials*. Put its client ID and client secret in the config. Notes:

- Google Desktop-app clients require `client_secret` in the token exchange even with PKCE — it is
  documented as non-confidential. Provide it via `clientSecret`.
- Google returns a refresh token only when the auth request carries `access_type=offline`; add
  `prompt=consent` to guarantee it on every interactive sign-in. Both go in `extraAuthParams`.
- Desktop-app clients allow **any loopback port**, so `redirect.port` can stay `0` (ephemeral) and
  no redirect URI needs registering.
- Google has no `groups` claim. To restrict a Workspace org, gate on the `hd` (hosted-domain)
  claim, e.g. `allowedClaims: [{ "claim": "hd", "values": ["yourcompany.com"] }]`.

### Okta / generic OIDC

Use issuer `https://<org>.okta.com/oauth2/default`, an **OIDC Native** app (Auth Code + PKCE,
loopback redirect allowed), leave `clientSecret` empty, and request a refresh token via the
`offline_access` scope instead of `extraAuthParams`. For group rules, have the IdP emit `groups`.

## id_token signature verification

`BasisRsaJwksTokenValidator` verifies RS256 against the issuer JWKS using only
`System.Security.Cryptography` (no third-party binary). It is injected via the `ISsoTokenValidator`
interface, so a `jose-jwt`–backed validator can be substituted (guarded by the
`BASIS_HAS_JOSE_JWT` define) if that library is added to the project.
