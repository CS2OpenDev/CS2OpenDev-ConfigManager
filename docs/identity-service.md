# The identity signing service

What `acb` expects of the CS2OpenDev service when it signs a backup for a Steam account (the `identity` level).
`acb`'s side is built and tested against a stand-in (`test/CS2OpenDev.ConfigManager.Cli.Tests/IdentityCommandTests.cs`);
the service is not built yet. Its own plan lives in `CS2OpenDev-WebService`.

`identity-service.openapi.yaml` (OpenAPI 3.1) is the same contract in a form both sides can build and test
against. When the two disagree, fix whichever is wrong; neither wins by default.

## Signing in

The service does not talk to Steam itself. Its identity provider (Keycloak or similar) brokers Steam's OpenID 2.0
login, and the API accepts only the provider's OIDC access tokens, with the player's SteamID64 in a claim the API
trusts.

`acb` signs in as a public client with the authorization code flow and PKCE (S256):

1. `GET {service}/v1/acb/config` returns where to sign in:

   ```json
   { "issuer": "https://login.cs2opendev.dev/realms/cs2opendev", "clientId": "acb", "scope": "openid" }
   ```

   `issuer` must be https (`acb` refuses anything else, as it does the service address, except a loopback
   address for testing).
2. `acb` reads `{issuer}/.well-known/openid-configuration` for `authorization_endpoint` and `token_endpoint`.
3. It opens the browser at the authorization endpoint with `redirect_uri=http://127.0.0.1:<port>/callback` (a
   port it picked and is listening on), a random `state` and the PKCE challenge. The provider's client must
   allow loopback redirects on any port (RFC 8252, section 7.3).
4. The browser comes back to the loopback port with `code` and `state`. A request with any other `state` is
   ignored; an `error` ends the sign-in.
5. `acb` posts the code, the redirect URI, the client id and the PKCE verifier to the token endpoint and keeps the
   `access_token` in memory for this run only. It is never written anywhere.

## Signing

`POST {service}/v1/acb/sign`, with `Authorization: Bearer <access_token>`:

```json
{ "manifestSha256": "<64 hex>", "platform": "windows-x64", "tool": { "name": "Advanced Config Backup (acb)", "version": "0.7.0" } }
```

That is all that is sent: the configuration itself never leaves the PC.

The service:

1. takes the SteamID64 from the token, never from the request;
2. builds the statement `acb` reads (below), with `signedAt` its own clock in UTC;
3. signs it with its current signing key, held in an HSM or key vault and never exported;
4. appends `(steamId64, manifestSha256, platform, tool, signedAt, keyid)` to an append-only log before answering;
5. returns the DSSE envelope as JSON, with `200`.

Any failure is a non-2xx status; `acb` reports the status and writes no backup.

### The envelope

A DSSE envelope with `payloadType` `application/vnd.in-toto+json` and exactly one signature:

```json
{
  "payloadType": "application/vnd.in-toto+json",
  "payload": "<base64 of the statement>",
  "signatures": [ { "keyid": "<signing key id>", "sig": "<base64 of r||s, 64 bytes>" } ]
}
```

The statement:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [ { "name": "manifest.json", "digest": { "sha256": "<manifestSha256>" } } ],
  "predicateType": "https://cs2opendev.dev/acb/trust/v1",
  "predicate": {
    "level": "identity",
    "steamId64": "7656119...",
    "signedAt": "2026-11-02T23:30:00+00:00",
    "delegation": { "payloadType": "application/vnd.cs2opendev.acb-delegation+json", "payload": "...", "signatures": [ ... ] }
  }
}
```

The signature is ECDSA P-256 over SHA-256 of DSSE's pre-authentication encoding of the payload type and payload,
in IEEE P1363 form (r and s, 32 bytes each). The `delegation` is the current one, unchanged, as the root holders
signed it. `acb` refuses unknown members anywhere in these documents.

`acb` checks the envelope before it writes the backup, exactly as a reader would: the subject must be the
manifest it sent, the delegation signed by enough roots it carries, and the key in its window and not revoked.

## Keys

The split follows TUF's, cut down.

- **Root keys**: three P-256 keys, each held offline by a different person. `acb` carries their public keys
  (`IdentityTrust.Shipped`) and needs two of the three on a delegation. There are none yet, so this `acb`
  refuses every identity backup, and refuses `--sign` (the backup screen greys it out) until they exist.
- **Signing keys**: in the service's key vault. A delegation lists each with a validity window
  (`notBefore`, `notAfter`) and, once revoked, `revokedFrom`.
- **Delegation**: a DSSE envelope (`application/vnd.cs2opendev.acb-delegation+json`) over
  `{ "_type": "https://cs2opendev.dev/acb/delegation/v1", "version", "issued", "keys": [...] }`, signed by the
  roots. Key ids are the SHA-256 of the key's SubjectPublicKeyInfo DER, in hex.

A signature counts if its key was in the delegation and in its window at `signedAt`, and not revoked by then.
`acb` also ships the revocations it knows (`IdentityTrust.Shipped.Revoked`), so a backup carrying an older
delegation still sees a later revocation once `acb` is updated; the earlier of the two times holds.

A stolen signing key can still sign with any `signedAt` inside its window until the revocation ships. The log is
what shows which signatures were real: a signature it does not hold was not made by the service.

### The ceremony

`tools/CS2OpenDev.ConfigManager.IdentityKeys` (`acb-identity-keys`), run offline:

```
acb-identity-keys new-key root1.pem                 each holder, on their own machine; prints the public key
acb-identity-keys delegate delegation.json --version 1 --key signing.pem,2026-12-01T00:00:00Z,2027-12-01T00:00:00Z
acb-identity-keys cosign delegation.json --root root1.pem      holder 1
acb-identity-keys cosign delegation.json --root root2.pem      holder 2
acb-identity-keys check delegation.json --root-public <spki1> --root-public <spki2> --root-public <spki3>
acb-identity-keys trust <spki1> <spki2> <spki3>     the lines for IdentityTrust.Shipped
```

`--key` takes the signing key's public PEM (a vault exports only that). Each new delegation needs a higher
`version` and two holders again. The roots go into `acb` in a release; replacing one needs a release too.

## Not decided

- Where the service runs, which identity provider, and which key vault; the service's own plan covers those.
- Fetching a newer delegation or revocation list online: `acb` does not yet; it relies on what it shipped with.
- DemoViewer.NET's plugin signing through the service: the plugin cannot load yet (design.md, "Loading the plugin
  in DemoViewer.NET").
