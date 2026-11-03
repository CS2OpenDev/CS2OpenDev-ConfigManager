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

### Which service

`acb` sends the token only to the service it was built to know, and signs in only at that service's identity
provider: both addresses are compiled in (`IdentityServiceAddress.Shipped`, beside the root keys). Neither exists
yet, so this `acb` signs through no service. `--identity-service` and `ACB_IDENTITY_SERVICE` are refused unless
they name that same address. Tests compile their stand-in in instead (`IdentityServiceAddress.Current`, which no
shipped source sets; a test checks that).

The reason is token relay. If `acb` took any service address, a hostile one could answer `v1/acb/config` with
the real provider and client id; the player would sign in on the genuine Steam page, `acb` would hand the
hostile service a real token, and it could sign any manifest as that player at the real service until the token
expired. A stand-in on this PC is no exception, even one that names a sign-in service on this PC: its sign-in
page can send the browser on to the real provider with `acb`'s own redirect, `state` and challenge, so the real
code comes back to `acb`, which would post it with the verifier to the stand-in's token endpoint, and a public
client's code is all the stand-in needs to get a real token. Any local process can listen on a loopback port,
another user's included. A stand-in has no honest use in a release either: `acb` checks the envelope against the
roots it ships, which only the real service can meet. With both addresses pinned and nothing else taken, a token
from the real provider only ever goes to the real service.

### The flow

`acb` signs in as a public client with the authorization code flow and PKCE (S256):

1. `GET {service}/v1/acb/config` returns where to sign in:

   ```json
   { "issuer": "https://login.cs2opendev.dev/realms/cs2opendev", "clientId": "acb", "scope": "openid" }
   ```

   `issuer` must be exactly the issuer `acb` knows.
2. `acb` reads `{issuer}/.well-known/openid-configuration`. Its `issuer` must equal the one above (OpenID
   Connect Discovery, section 4.3), and `authorization_endpoint` and `token_endpoint` must be on the issuer's
   own scheme, host and port. Otherwise `acb` stops before opening or sending anything, so a provider that
   advertises `http://` endpoints (Keycloak behind a TLS proxy without forwarded headers, say) fails at once
   instead of sending the code and the token in the clear.
3. It opens the browser at the authorization endpoint with `redirect_uri=http://127.0.0.1:<port>/callback` (a
   port it picked and is listening on), a random `state` and the PKCE challenge. The provider's client must
   allow loopback redirects on any port (RFC 8252, section 7.3). `acb` hands the system only an https address
   to open (or http on this PC, for a test's stand-in), and always prints it as well, so it can be opened by hand in
   a browser on the same PC when none starts. The backup screen shows it in a box with Cancel and signs in off the
   screen's thread; at the terminal Ctrl+C stops the wait, writes nothing and deletes the temp copies of the
   settings, and a second Ctrl+C ends `acb` at once (leaving those copies) when a step, such as a prompt, does
   not stop on the first. On Linux `acb` counts the browser as opened only when `xdg-open` does not fail within a few seconds.
   The redirect comes back to this PC, so a browser on another PC can never finish the sign-in: `--sign` is
   refused, and the backup screen greys it out, in an SSH session (`SSH_CONNECTION`, `SSH_CLIENT` or `SSH_TTY`
   set) and on Linux with no desktop (neither `DISPLAY` nor `WAYLAND_DISPLAY`), before anything is captured.
   `backup --sign` with no terminal is refused at once rather than waiting for a sign-in nobody can do.
4. The browser comes back to the loopback port with `code` and `state`. A request with any other `state` is
   ignored; an `error` ends the sign-in. Each connection to the port has ten seconds in all to send its request,
   or less when the sign-in limit comes sooner (one deadline, not one per read), and Cancel or the sign-in limit
   ends it mid-read, so another program on the PC that connects and sends a byte now and then cannot hold the
   sign-in open. Connections are read one at a time, so such a program can keep the browser's callback waiting
   until the limit. That is accepted: it only ends the sign-in with nothing written, and a program that can do it
   could as well stop `acb` itself.
5. `acb` posts the code, the redirect URI, the client id and the PKCE verifier to the token endpoint and keeps the
   `access_token` in memory for this run only. It is never written anywhere.

Every answer is read strictly: a member missing or null, a body that is not JSON, or a non-2xx status ends the
sign-in with a message naming the step that failed, and no backup is written. So does nobody signing in within
five minutes. A failure status is put in the contract's words, from the status and `Retry-After` only (the
problem body is not read): 401, the sign-in was not accepted or expired, so run again; 403, this account may not
sign; 429, too many signatures, try again in the minutes `Retry-After` gives; 5xx, the service cannot sign now.
The 403 and 429 meanings are the sign request's only: on the config, discovery and token steps they are reported
as a refused request and a busy service, since a proxy or the identity provider may send them for other reasons.
Each says that no backup was written and that an unsigned or sealed one can be saved now.

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
4. appends `(steamId64, manifestSha256, platform, tool, signedAt, keyid)` to an append-only log before answering
   ("The log", below);
5. returns the DSSE envelope as JSON, with `200`.

Any failure is a non-2xx status; `acb` reports the status and writes no backup.

### The envelope

A DSSE envelope with `payloadType` `application/vnd.in-toto+json` and the service's one ECDSA P-256 signature:

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
signed it. `acb` refuses unknown members anywhere in these documents, saying the backup is damaged or from a
newer version, which may read it; an `acb` that signs and gets such an answer says to update and sign again. So
adding a member is a change every reader in the field refuses until it is updated.

An envelope may carry up to four signatures. Exactly one must be by a P-256 key the delegation lists; two of
those are refused, so the signer is never in doubt. Any other signature (by a key the delegation does not list,
or lists with another kind of key) is not checked and never makes a backup check on its own. That is room for a
post-quantum signature (ML-DSA) beside the ECDSA one later: an `acb` that does not know it still reads the
envelope by its ECDSA signature, and a later `acb` can require both.

`Identity.CheckEnvelope` (in `CS2OpenDev.ConfigManager.Core`) is the whole check a reader runs, as one public
function of the signature file, the manifest's digest, the trust and the time. The service's tests run it on the
service's own answers, against test roots and a delegation made with `CS2OpenDev.ConfigManager.IdentityIssuer`,
so a difference from what `acb` reads (DSSE lengths counted in characters rather than bytes, base64url, an extra
member) shows there rather than at the first real signing.

`acb` checks the envelope before it writes the backup, exactly as a reader would: the subject must be the
manifest it sent, the delegation signed by enough roots it carries, and the key in its window and not revoked.
Then it compares the signer with the account whose settings it backed up. When they differ, the person signed
in as someone else, or (on Linux, where any local user can read the browser's command line, `state` and
`code_challenge` included) another user's sign-in for their own account reached the loopback port first. `acb`
says so, giving both accounts as account ids and SteamID64s so they can be told apart, and asks before saving.
When the settings name no Steam account (neither the CS2 settings nor the launch options are in the backup), there is nothing to compare,
so `acb` names the account that signed and asks the same way. With no yes, and on the backup screen, which does
not ask, nothing is written.

## Keys

The split follows TUF's, cut down.

- **Root keys**: three P-256 keys, each held offline by a different person. `acb` carries their public keys
  (`IdentityTrust.Shipped`) and needs two of the three on a delegation. There are none yet, so this `acb`
  refuses every identity backup, and refuses `--sign` (the backup screen greys it out) until they exist and the
  service's address ships beside them.
- **Signing keys**: in the service's key vault. A delegation lists each with a validity window
  (`notBefore`, `notAfter`) and, once revoked, `revokedFrom`. Windows are short, about a month, and one
  delegation lists the next several keys in turn, so the holders need not meet each month and revoking one key
  (below) costs at most a month of honest backups: every `acb` that has the revocation refuses those backups
  outright (`verify`, `show`, `plan` and `restore` alike), whatever the policy, and says that honest backups
  signed with the key are refused too, that their files are still plain text in the zip, and that a new backup
  is the remedy. A key is destroyed in the vault once its window ends, so it cannot leak later. A backup is read
  long after that: its signature is checked at `signedAt`, never at the reading time, so it keeps checking.
- **Delegation**: a DSSE envelope (`application/vnd.cs2opendev.acb-delegation+json`) over
  `{ "_type": "https://cs2opendev.dev/acb/delegation/v1", "version", "issued", "keys": [...] }`, signed by the
  roots. Key ids are the SHA-256 of the key's SubjectPublicKeyInfo DER, in hex.

A signature counts if its key was in the delegation the backup carries and in its window at `signedAt`, not
revoked by that delegation's `revokedFrom` by then, and not among the stolen keys `acb` ships
(`IdentityTrust.Shipped.Revoked`).

`signedAt` is written by whoever holds the signing key, so every check against a time binds only an honest
signer. A thief dates a forgery inside the key's window and before any revocation time. That is why a shipped
revocation has no time: a key on the list counts for no signature at all, the honest ones it made included, and
`acb-identity-keys revoke <keyid>` prints the line for a release. A delegation's `revokedFrom` only records when
the service stopped using a key.

Each backup carries its own delegation, and `acb` checks it against that one alone. It does not compare
`version`, which orders delegations for the ceremony and the service. So a new delegation that drops or revokes
a key binds only backups signed under it; someone holding a stolen key attaches the old one. Only the shipped
list stops a thief, and only in a reader that has it. A minimum delegation version in `acb` would not help,
because honest backups signed under the older delegation would fail along with the forgeries.

A revocation reaches only readers the thief does not choose. A self-contained backup carries its own `acb` as
`restorer/acb(.exe)`, and Windows trusts that by its code signature, which an older genuine release still has. So
a thief can put a genuine `acb` from before the revocation in a forged backup, and anyone who checks the backup
with that restorer sees it check. The DemoViewer.NET plugin carries its own copy of Core, on its own release
cadence, and is a third place the list must ship. So:

- Every identity verdict (the commands' `Signed` lines, `--json`'s `revokedAsOf`, the restore screen, the plugin)
  names the day this build's revoked keys date from (`IdentityTrust.Shipped.RevokedAsOf`, set in each release
  with the list), and says a key stolen since still checks there.
- When the `acb` checking a backup is byte for byte the restorer the backup carries, it says so: whoever made the
  backup chose that `acb`, and with it the stolen keys it knows of, so the signature is checked again with an
  `acb` installed on its own, at its newest release.
- A revocation ships in `acb` and in the plugin.
- An organiser's policy can refuse the copy of `acb` a backup carries (`refuseCarriedRestorer`) and any `acb`
  older than a release (`minimumAcbVersion`), so a genuine but stale `acb` does not check backups at their event.

A backup signed with a revoked key is not hidden: `acb` shows the signer it names, says the key was revoked, and
that the files still match, and lets the person (or the organiser's policy, or an administrator overriding it)
decide whether to use it at the hash level (design.md, "Organiser policy").

Until the revocation ships, and in every reader without it, a stolen key signs backups that check. The log is
what shows which signatures were real while it lasts: a signature it does not hold was not made by the service.

### The log

The log exists to tell the service's own signatures from a stolen key's, so an entry is needed only while a
forgery under its key could still be questioned. The contract asks of the service:

- **Retention**: an entry is kept until its key's `notAfter` plus one year (time to notice and revoke a theft,
  and to settle disputes over backups made before it), then deleted.
- **Access**: read only for incident response (a suspected stolen key, or a disputed backup) by the service's
  operators, and each read is itself recorded.
- **Addresses**: client IP addresses serve rate limiting only, kept in memory or a short-lived store, and are
  never written to the log with entries.

### The ceremony

`tools/CS2OpenDev.ConfigManager.IdentityKeys` (`acb-identity-keys`), run offline. Its commands are `Ceremony` in
`tools/CS2OpenDev.ConfigManager.IdentityIssuer`, the signing side of the level, which the tests and the service
reference and `acb` and the plugin do not:

```
acb-identity-keys new-key root1.pem                 each holder, on their own machine; prints the public key
acb-identity-keys delegate delegation.json --version 1 --key signing.pem,2026-12-01T00:00:00Z,2027-12-01T00:00:00Z
acb-identity-keys cosign delegation.json --root root1.pem      holder 1
acb-identity-keys cosign delegation.json --root root2.pem      holder 2
acb-identity-keys cosign delegation.json --root root3.pem      holder 3
acb-identity-keys check delegation.json --root-public <spki1> --root-public <spki2> --root-public <spki3>
acb-identity-keys trust <spki1> <spki2> <spki3>     the lines for IdentityTrust.Shipped's roots
acb-identity-keys revoke <keyid>                    a stolen key's line for IdentityTrust.Shipped's revocations
acb-identity-keys pin delegation.json               a delegation's line for IdentityTrust.Shipped's pins
```

- Each holder runs `new-key` and `cosign` on a machine that is offline and not synced to a cloud or backup
  service, or keeps the key on a hardware token. `new-key` writes the key only encrypted (PKCS#8, AES-256,
  PBKDF2-SHA256 at 600,000 iterations) under a passphrase typed without echo, readable by its owner only on
  Linux; `cosign` asks for that passphrase and takes no unencrypted key.
- `cosign` first shows the delegation's version, its issue time, every key with its id and window, and the ids
  already on it, and refuses one that is not a delegation, whose keys are not P-256 under their own ids, or
  that carries a signature whose id is not 64 hex digits. So everything it prints from the file is a number, a
  time or a hex id, and nothing in the file can move the cursor to paint over what the holder compares; a
  refusal that quotes the file writes control characters as escapes. It signs only once the holder types the
  version back. Each holder compares the key ids with a list the service's
  operators give them by another channel than the file, so a delegation swapped between holders, or on the
  machine that wrote it, is caught before it is signed. 2-of-3 survives one bad party only if every holder looks.
- `check` needs at least two roots (`--threshold` below 2 is refused) and prints how many of the given roots
  actually signed, and which. Roots given to it must be P-256.
- `--key` takes the signing key's public PEM only and refuses a private one, so the service's private key never
  leaves its vault. Its window must end after it starts.
- Each new delegation needs a higher `version`: above version 1, `delegate` needs `--previous <the current
  delegation.json>` and refuses a version that is not above it.
- Every holder whose root is still in use signs each delegation, not just two. A backup keeps its delegation for
  good and cannot be signed again, so a delegation two of three roots signed would drop below the threshold, in
  every later `acb`, the day one of those two is replaced. Signed by all three, it survives any one replacement.
  `check` passes only once every root it checks signed. It checks the roots this build ships, plus any
  `--root-public` (a root a coming release adds); a shipped root is left out only by `--without <keyid>`, which it
  prints, so a holder who did not cosign cannot pass by being left off the command line. Before the first roots
  ship, the roots are the ones given. A signature by a root a reader does not know is skipped, so extra
  signatures on a delegation cost nothing. Extra roots in a release are not free: every shipped root counts
  toward the threshold for any delegation, new ones included.
- The roots go into `acb` in a release, and replacing one needs a release too. So does a revocation that must
  stop a thief (above). A root set may ship only while every delegation a backup may carry still meets the
  threshold under it. A root leaves the shipped roots in the next release when its key is lost or destroyed,
  when its holder leaves (with or without the key: a holder who leaves is treated as if the key were stolen), or
  when it may have been stolen. A root kept after its holder left would count toward the threshold for every
  new delegation, since `issued` is root-signed and can be backdated, so each departure would add a person who,
  with one other former holder, could delegate a key of their own. The release that removes a root pins every
  delegation made so far by the SHA-256 of its payload (`IdentityTrust.Shipped.Pinned`; `acb-identity-keys pin`
  prints the line, and only for a delegation the shipped roots still accept). A pinned delegation checks without
  counting root signatures, so old backups keep checking, while the removed root vouches for nothing new. Its
  keys still meet the revocation list. An `acb` from before the removal still trusts the removed root; no
  shipped build can be changed, which is the cost of a departure and why the revocation list and the newest
  `acb` matter. While roots change, each new delegation is signed by every holder still in use of the old set
  and of the new, so it meets the threshold in an `acb` from before the change and one from after.
- `cosign` checks a signature already under the holder's id: one that checks against this delegation leaves the
  file as it was ("already signed by you"), and one that does not (left from another payload, or planted) is
  replaced, and the holder is told so. It writes the file through a new file moved over the old one, so an
  interrupted write leaves the old one whole.
- `trust` refuses a key that is not a P-256 SubjectPublicKeyInfo, and the same key twice, so a truncated key is
  caught there. Every delegation the holders make is checked in under
  `test/CS2OpenDev.ConfigManager.Core.Tests/ShippedDelegations/`, and a test checks the shipped trust against
  them in every build: P-256 roots under their own ids, each once; revoked ids and pins that are 64 hex digits;
  and once there are roots, a threshold from 2 to the number of roots, a `RevokedAsOf`, at least one delegation
  checked in, every one of them accepted, every shipped root among the signers of the newest (so a valid key
  that is the wrong one, or a holder who never cosigned, fails the build), every revoked id a key some
  delegation lists, and every pin a checked-in delegation's digest. With no roots, nothing may be revoked or
  pinned.
- An option without its value, a one-value option given twice, or a number out of range is refused, never
  dropped or crashed on.
- A delegated key must be P-256 in the one SubjectPublicKeyInfo encoding (named curve, uncompressed point),
  matched byte for byte, so a key on another 256-bit curve that one platform's library reads and another's
  does not is refused on both.

## Not decided

- A signing time that does not come from the signer: an RFC 3161 timestamp from an independent authority, or an
  inclusion proof in a public transparency log, checked offline. With one, a shipped revocation could take
  effect from the theft and keep the honest backups made before it; without one, it voids the whole key.

- Where the service runs, which identity provider, and which key vault; the service's own plan covers those.
- Fetching a newer delegation or revocation list online: `acb` does not yet; it relies on what it shipped with.
- Conformance files checked into the repository (test roots, a delegation, a valid envelope and named bad ones)
  and a test that checks them against the OpenAPI schemas. That needs a JSON Schema validator as a test
  dependency; until then the service runs `Identity.CheckEnvelope` on its own answers.
- Sender-constrained tokens (DPoP, RFC 9449) and a token audience pinned to the sign endpoint (RFC 8707). With
  both addresses compiled into `acb` a token is not relayed through another service, but a DPoP-bound token
  would also be useless to anyone who copied it out of the process. It needs the provider's support, so the
  service's plan decides.
- Erasure against the append-only log: whether a player's request to erase their entries is honoured before
  retention ends, and how that fits the log's purpose.
- DemoViewer.NET's plugin signing through the service: the plugin cannot load yet (design.md, "Loading the plugin
  in DemoViewer.NET").
