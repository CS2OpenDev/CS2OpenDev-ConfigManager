# CS2OpenDev ConfigManager: design of record

Status: design, 2026-11-01. The product is Advanced Config Backup: a standalone tool first, with a first-party
DemoViewer.NET plugin from this repository. It started inside DemoViewer.NET as an extension and moved here
before it was merged. This design replaces the script restorer described in [v1-reference.md](v1-reference.md). What
v1 captures and why (the section table, what is left behind on purpose, secret removal, hardware-key merges)
carries over unless this document says otherwise; how a backup is packaged, trusted and restored does not.

## Why v1's restorer is replaced

v1 ships its restorer as editable source: `Restore.ps1` plus `kit\*.cs`, compiled on the target by Windows
PowerShell 5.1. Most of v1's hardening (a hash-pinned elevation bootstrap, folder ownership checks before
running elevated, the restore-or-undo decision read from the manifest) exists only because code and data share
one editable folder. Each patch closed one route and left the cause, and two open problems come straight from
it: an administrator typing a password at the UAC prompt is refused, and a manifest that says `kind "undo"`
skips the restore's path policy.

v2 separates the two:

- **Trusted code** is a compiled, signed restorer. Nothing in an archive changes what it does.
- **Data** is a plain-text archive that anyone can read, that is tamper-evident, and that the restorer
  treats as untrusted input whatever its signature says.

## Principles

1. **An archive is data, never code.** The restorer's own rules decide what may be written. An archive can
   ask for less than the rules allow, never more.
2. **Archives are immutable.** Any change to any file after creation fails verification, and the restorer
   refuses it. There is no override; the player makes a new backup.
3. **Readable by a person.** A zip of text files a player or admin can open and check before restoring.
4. **One platform per archive.** A Windows backup restores on Windows, a Linux backup on Linux. No mapping
   between them; the restorer refuses a platform mismatch before reading anything else.
5. **When in doubt, change nothing and ask.** Interactive runs ask, with "leave it" as the default. Headless
   runs take that default and exit non-zero.
6. **The restorer owns its undo records.** They live where the restorer puts them, never beside the backup.
7. **No signing key in any shipped app.** DemoViewer and the restorer carry public keys only.

## Components

```
src/CS2OpenDev.ConfigManager.Core/          capture, restore engine, policy, archive v2, verification
src/CS2OpenDev.ConfigManager.Cli/           `acb`: its screens (TUI) and headless commands (Native AOT)
plugin/CS2OpenDev.ConfigManager.DemoViewer/ the DemoViewer.NET plugin: the Settings page that makes backups
test/<project>.Tests/                       one TUnit project per component
```

- **Core** targets `net10.0`, is marked `IsAotCompatible`, and references nothing from DemoViewer.NET or
  Avalonia. The C# 5 and ASCII rules of v1's kit went with PowerShell; the kit keeps its style until it is
  reworked. `Restoring/` holds what the restorer adds around the engine: the archive as the engine's source,
  the undo store and its runner, the organiser's policy, and the administrator's store ACL. Only capture,
  archive writing and reading, and the policy are public, for the plugin; the restore and undo engine, its
  sources and options, the undo store and its runner are internal to Core, `acb` and their tests, so nothing
  else can restore from a folder or undo from a record outside the store. The engine itself refuses a restore
  that would change anything without an undo store, whoever builds its options. The kit runs with nullable checks
  off, and each kit file turns off by name only the style rules v1's dialect breaks; the platform, disposal,
  security, culture and trimming analyzers run there as everywhere else, and a test refuses a bare
  `#pragma warning disable`.
- **`acb`** is published Native AOT for `win-x64` and `linux-x64`: one binary, no .NET install. It makes,
  checks, restores and undoes backups, so organisers and Linux players do not need DemoViewer.NET.
- **The plugin** references Core directly and builds against the published `DemoViewer.NET.Extensions.Sdk`
  and `.Sdk.Ui` packages, with the Avalonia, CommunityToolkit.Mvvm and Microsoft.Extensions versions that SDK
  pins (the host refuses a plugin whose references differ from its own). Its id is
  `dev.cs2opendev.configmanager`, outside DemoViewer.NET's reserved `net.demoviewer.` prefix, and its
  contribution ids start with that id, so it meets the rules for a plugin that is not bundled with the app.

### Loading the plugin in DemoViewer.NET

DemoViewer.NET 0.10 resolves only extensions it ships with (`ExtensionLoader.Resolve` walks its
`ShippedPack` list; staged downloads replace a shipped pack, never add one). A plugin built in another
repository therefore needs one of two changes on the DemoViewer.NET side, which belong to that repository's
own work and are not made from here:

1. **Declared first-party pack**: DemoViewer.NET names this plugin's id, its update feed (this repository's
   GitHub releases) and its publisher key, and installs it through the existing update and signing path
   on first use instead of bundling it.
2. **Installed extensions**: DemoViewer.NET gains a general install path for extensions it does not ship,
   with the signing and trust policy deciding what loads.

Until then the plugin is built and tested here, and the restorer is the product.

## Archive format v2

A zip. The zip is only a container: every trust decision is made against `manifest.json`.

```
manifest.json            what is in the archive: every entry with its size and SHA-256
signature.dsse.json      a DSSE envelope over a statement naming manifest.json's digest
README.txt               what this is, the trust level, how to check and restore it
files/<section>/...      captured files, byte for byte (after secret removal)
capture.vdf              the capture record: every section's settings that are not files (registry,
                         NVIDIA, power, launch options) and the file list, in Valve KeyValues
restorer/acb[.exe]       optional: the restorer for this archive's platform ("self-contained")
```

`manifest.json`:

```json
{
  "format": "acb/2",
  "platform": "windows-x64",
  "created": "2026-11-01T18:02:11Z",
  "tool": { "name": "Advanced Config Backup", "version": "0.2.0" },
  "steamAccount": "69413322",
  "sections": [
    { "id": "cs2", "label": "CS2 settings for account 69413322", "notes": ["secrets removed from 2 files"] }
  ],
  "entries": [
    { "path": "files/cs2/local/cfg/cs2_user_keys_0_slot0.vcfg", "size": 4211, "sha256": "..." }
  ]
}
```

`signature.dsse.json` is a [DSSE](https://github.com/secure-systems-lab/dsse) envelope. Its payload is an
in-toto style statement:

```json
{
  "_type": "https://in-toto.io/Statement/v1",
  "subject": [{ "name": "manifest.json", "digest": { "sha256": "..." } }],
  "predicateType": "https://cs2opendev.dev/acb/trust/v1",
  "predicate": { "level": "hash" }
}
```

`capture.vdf` keeps v1's capture record in the same text format as CS2's own config files, so the engine
that reads it is v1's, already tested, rather than a rewrite. It is listed in `manifest.json` like every
other entry.

The signature covers DSSE's pre-authentication encoding of the payload, so `manifest.json` stays plain,
readable and unchanged, and the envelope stays small.

### Strict reading

Signed JAR and APK v1 broke on zip ambiguities, not on cryptography. The reader therefore:

- reads the central directory once and rejects duplicate names (case-insensitively on Windows), absolute
  paths, `..`, drive letters, backslashes, entries over the size limit, and encrypted or unusual
  compression methods;
- requires the set of zip entries to equal `manifest.entries` plus `manifest.json` and
  `signature.dsse.json`, exactly: nothing extra, nothing missing;
- checks every entry's size and SHA-256 while streaming it, and never extracts to a folder the restorer
  then trusts. Restores read from the verified bytes in memory or from a private temp copy made after
  verification;
- refuses `format` values it does not know and any `platform` other than the one it runs on;
- refuses, in every JSON document, a member it does not know, a required member missing, and a null where the
  format has none, so a crafted file is refused rather than read as nulls;
- allows under `restorer/` only the one restorer for the archive's own platform (see "Self-contained archives").

## Trust levels

Every archive has exactly one level, named in the statement's predicate and shown before anything else.

| Level | Proves | Needs | Who can check |
|---|---|---|---|
| `hash` | the files match the manifest (damage, accidental edits) | nothing | anyone; says "unsigned: anyone could have edited this" |
| `seal` | made by someone who knows the passphrase, unchanged since | a passphrase | only someone who knows the passphrase |
| `identity` | made by this Steam account, unchanged since | the web service at creation; nothing at restore | anyone, offline |

A valid signature says who made a backup, not that it is safe. The restorer's rules apply at every level.

### `seal`: passphrase

For a player moving their own settings between their own PCs, with no account and no server.

- Key: Argon2id over the passphrase (memory 64 MiB, 3 passes, 1 lane; a 16-byte random salt). The
  parameters and salt sit in the signed predicate, so changing them only fails the check.
- Signature: HMAC-SHA256 over DSSE's encoding of the statement, `keyid` `"seal"`.
- Nothing is stored. DemoViewer does not keep the passphrase.
- Limit, stated in the UI: anyone with the archive can try passphrases offline, and a guessed passphrase can
  forge a seal. The page offers a generated phrase of six words and refuses one under 12 characters.
- Not for organisers: checking needs the secret.

The predicate of a sealed archive:

```json
{
  "level": "seal",
  "kdf": { "name": "argon2id", "memoryKiB": 65536, "iterations": 3, "parallelism": 1, "salt": "<16 bytes, base64>" }
}
```

In detail:

- The key is Argon2id (RFC 9106, version 0x13) over the passphrase's UTF-8 exactly as typed, 32 bytes. Nothing is
  trimmed or normalised: `acb` is built with invariant globalisation, where Unicode normalisation does nothing, so
  normalising in the plugin alone would give one passphrase two keys. An accented letter typed another way therefore
  does not open the seal, and sealing with any character beyond ASCII is warned about. Length is counted in
  characters as a person sees them (text elements).
- A reader tries only the settings above. A `kdf` with others, a salt that is not 16 bytes, a `kdf` on a `hash`
  statement, or anything but exactly one signature with `keyid` `"seal"` is refused before a key is made, so an
  archive cannot set how much memory or time a check spends, or weaken the settings to make guessing cheaper.
- A wrong passphrase and an archive changed after sealing look the same, and are refused with one message. `acb`
  asks at the terminal (hidden), again after a wrong passphrase, three tries in all; `--passphrase-file` is read
  once. Without either the refusal says how to give one. The screens ask for it in a box when a backup turns out to be sealed.
- The administrator's run is handed the 32-byte key over its pipe beside the backup's bytes, never the
  passphrase, and asks nobody. The key opens that archive only, as its salt is the archive's own.
- Anyone can strip a seal and rewrite the statement at `hash`: such a backup reads as `hash`, and a policy whose
  `minimumLevel` is `seal` refuses it.
- Generated phrases are six words from the EFF's large list (7,776 words, about 77 bits), picked with the system's
  random number generator (`THIRD-PARTY-NOTICES.md`).
- The plugin carries the two Konscious assemblies beside its DLL and no assembly the host loads itself.

### `identity`: signed by a Steam account

The model is Sigstore's (sign in with an identity, a service vouches for it, check offline) with Steam as
the identity provider. `docs/identity-service.md` is the contract the service is built against.

1. The player signs in to the CS2OpenDev service. The service's identity provider brokers Steam's OpenID 2.0
   login, and `acb` signs in to the provider with the authorization code flow and PKCE through the browser,
   receiving the code on a loopback port. The access token lives only in that run, and goes only to the service
   whose address, and whose provider's issuer, are compiled into `acb` beside the root keys (none yet), so a
   service named on the command line cannot relay it. No other service is taken, not even a stand-in on the PC's
   loopback address, which could send the browser on to the real provider and swap the code for a real token;
   tests compile their stand-in in. Signing needs a person at the terminal or the screens; headless `--sign` is
   refused, and so is `--sign` where the browser cannot be on this PC (an SSH session, or Linux with no desktop),
   because the sign-in comes back to a port on this PC. Ctrl+C stops a sign-in at the terminal with nothing
   written; a second Ctrl+C ends `acb` at once, for a step that does not stop on the first. A failure the service reports is put in plain words (not allowed to sign, too many signatures and
   when to retry, the service is down), with the unsigned or sealed backup as the way to save one now.
2. `acb` (`backup --sign`, or "Sign it for my Steam account" on the backup screen) sends the manifest's SHA-256,
   the platform and the tool version. The configuration itself never leaves the PC.
3. The service builds the statement with predicate `{ level: "identity", steamId64, signedAt, delegation }`,
   taking the SteamID64 from the token, signs it with ECDSA P-256 using a key held in an HSM or key vault, and
   returns the envelope. It records `(steamId64, digest, time)` in an append-only log, kept for the key's
   window plus a year (`docs/identity-service.md`, "The log").
4. `acb` checks the envelope before writing the backup, as any reader would, and every reader checks it offline
   against the root keys built into it. A signer other than the account whose settings were backed up (another
   sign-in, perhaps another local user's, reached `acb` first) is saved only when the person says yes at the
   terminal; the backup screen writes nothing and says why.

Key management follows [TUF](https://theupdateframework.io/)'s split, cut down:

- **Root keys** (three, offline, two needed, and every holder signs each delegation so that replacing any one
  root later leaves old backups' delegations at the threshold) are compiled into `acb` (`IdentityTrust.Shipped`).
  A root whose key is lost, whose holder leaves or that may be stolen is removed in the next release, which pins
  every delegation made so far by its payload's SHA-256 (`IdentityTrust.Shipped.Pinned`), so old backups keep
  checking and a removed root never vouches for a new delegation. None exist yet:
  until the key ceremony (`tools/CS2OpenDev.ConfigManager.IdentityKeys`) makes them, `acb` refuses every identity
  backup and refuses to sign one.
- Root keys sign a **delegation**: the current signing keys, each with a validity window. The predicate carries
  the delegation that was current when it was signed, so checking needs no network, and the zip's layout does
  not change.
- A signature counts if its key was delegated and in its window at `signedAt`, and not revoked by then. The
  signer writes `signedAt`, so those times bind only an honest signer. A backup is checked against the
  delegation it carries, whose version `acb` does not compare, so a new delegation that lists a key as revoked
  binds only the backups signed under it. A compromised key is stopped by the revocation list each release
  ships (`IdentityTrust.Shipped.Revoked`; the ceremony tool prints the line), which overrules every delegation
  once `acb` is updated and voids every signature the key made, whatever its date. Signing keys have short
  windows, so that costs little in signatures, but no updated `acb` checks the backups it voids as signed. Such a backup is still shown, with the signer it names, a warning that the key was
  revoked, and that its files match its manifest, so it is unchanged since it was made but who made it is not
  proven. Using it is a decision: `acb` asks (headless, `--accept-revoked`), and on a yes reads it at the hash
  level, never as signed, with the claimed signer recorded in the report. Under a policy that needs a signature it
  is refused unless the policy says `"revokedSignatures": "ask"`, or an administrator gives `--accept-revoked`
  (acb elevated on Windows, root on Linux) for that one restore. Fetching the list online is not built. The list reaches only readers the thief
  does not choose: a forged self-contained backup can carry a genuine `acb` from before the revocation. So every
  identity verdict names the day its revoked keys date from, and an `acb` that is the restorer the backup carries
  says to check again with one installed on its own.
- Refusals that may only mean the reader is older than the backup (a manifest format, a payload type, a trust
  level, a statement or a delegation it does not read, including one with a member it does not know, or a
  delegation under roots it does not know) say a newer version may read it, and the DemoViewer plugin reports
  them as backups it cannot check yet, not as unsafe ones. A delegation whose signature by a root this version
  knows does not check is refused as changed: a keyid is the key's hash, so no newer version would read it.
- Delegated and root keys are P-256 by their exact SubjectPublicKeyInfo encoding, so every platform takes the
  same keys. The ceremony shows a holder what a delegation vouches for, and signs only once they confirm it.
- Signatures are ECDSA P-256 over SHA-256, as r||s, over DSSE's pre-authentication encoding. ECDSA P-256 is in the
  .NET base library on both platforms and works under Native AOT. A post-quantum signature (ML-DSA, in .NET 10
  where the OS supports it) can be added beside it later as a second signature in the same envelope: an envelope
  may carry up to four, exactly one by a P-256 key the delegation lists is checked, and the others are left alone.

Every command, the restore screen and the plugin's "Check a backup" name the signer, the time and the key, and
say when the settings are another account's than the signer's, in both the account id and SteamID64 forms.
Before saving a signed backup, `acb` asks when the signer is not the settings' account, or when the settings name
no account to compare with; the backup screen, which does not ask, writes nothing then. The signing side
(delegations, statements, the ceremony) is a separate library, `tools/CS2OpenDev.ConfigManager.IdentityIssuer`,
that neither `acb` nor the plugin carries. A policy's `steamIds` accept a backup only when it is identity-level and signed by one of them.

### Organiser policy

An organiser restoring many players' backups passes a policy file. A policy can only narrow the built-in
rules, never widen them.

```json
{
  "minimumLevel": "identity",
  "steamIds": ["76561198029679050", "76561197960287930"],
  "sections": { "allow": ["cs2", "cs2game", "launch", "nvidia"] },
  "nvidia": { "deny": ["0x10835002"] },
  "requireCs2Closed": true,
  "revokedSignatures": "refuse",
  "refuseCarriedRestorer": true,
  "minimumAcbVersion": "1.0.0"
}
```

Every field takes away: a higher level, fewer signers, fewer sections, fewer NVIDIA settings, and with
`requireCs2Closed` a running CS2 refuses the whole restore instead of leaving its sections.
`revokedSignatures` (`refuse` or `ask`) says what happens to a backup signed with a revoked key; without it,
`refuse` when the policy needs a signature (`minimumLevel` above `hash`, or `steamIds`) and `ask` otherwise, as
with no policy. Under `refuse`, an administrator's `--accept-revoked` applies one anyway, and `steamIds` are then
compared with the account the revoked signature names. `refuseCarriedRestorer` refuses checks and restores by
the copy of `acb` a self-contained backup carries, which whoever made the backup chose, along with the revoked
keys it knows; `minimumAcbVersion` refuses an older `acb`. Both stop a genuine but stale `acb`; neither can stop a
modified one, which is why an organiser runs their own. Every released `acb` knows these fields, so none half-applies
a policy that has them.

A player can pass the event's policy to `acb backup --policy`, which refuses up front a backup the policy would
refuse (too low a level, unsigned where Steam IDs are named, a signature for an account it does not name, only
parts it does not restore) and says which parts it will leave and whether the backup's own `acb` may restore
it. That helps an honest player; it enforces nothing, as the maker controls their own PC. The restore side does. A field the restorer
does not know, a misspelt section, a malformed id, comments or trailing commas refuse the file, so a policy is
never half-applied. `steamIds` is checked against a signature, never the manifest's own account: the reader
hands over the signer it verified, and a backup below `identity`, with no verified signer, or signed by an account
the list does not name is refused. Until `identity` is turned on, a policy that names Steam IDs refuses every backup.
A policy or plan file saved with a byte order mark, or as UTF-16 (Windows PowerShell 5.1's `>`), is read by its
mark; a policy's digest is over the file's own bytes.

## The restorer

### Rules are compiled in

Per platform, `acb` carries the allowlist: which paths under which roots, which registry values with which
kinds, which NVIDIA setting IDs with which types and sizes, which power plan operations. v1's capture-time
and restore-time rules (`IsAccountSetting`, `IsPlayerCfg`, `IsRestorablePath`, the registry and NVIDIA
allowlists, hardware-key merges) move into Core as one rule set used by both capture and restore.

A path is judged by the names it is written with, so no name may stand for another: one that ends in a dot or a
space, which Windows drops, is neither captured nor restored, and a backup's name that could be a Windows 8.3
alias (at most eight characters, a dot and three, with a tilde and a digit, as in `GAMEST~1.CFG`) is never
restored. Capture reads the folder's own long names, so a player's `nade~1.cfg` is carried, and a restore names
it as left alone. An archive that names one file, one NVIDIA setting (in any spelling of its id) or
one registry value twice fails that part before anything is recorded or written, since the second entry would be
recorded over the first and the undo record would keep the restore's own value. Within one record, the first
value recorded for a target is never replaced, and an undo writes a part's registry values newest first.

Whether a run is a restore or an undo comes from the command (`acb restore`, `acb undo`), never from the
archive. An undo record is never an archive; it is read only from the restorer's own store. The engine takes the
mode as a parameter and reads its input through a source: a verified archive's bytes in memory, or an undo
record's folder. The bytes it checks against `capture.vdf` are the bytes it writes; nothing is unpacked.

### Commands

```
acb                                  the screens (TUI): back up, restore, put this PC back
acb verify  <archive> [--policy f]   check only; prints level, signer, sections, entries
acb show    <archive> [--section s]  list or print captured files and values
acb plan    <archive> [options]      what a restore would change, as text or --json
acb restore <archive> [options]      restore (asks unless --yes)
acb restore --plan plan.json         apply exactly a reviewed plan, refusing if the PC changed since
            [--policy f] [--gsi]     the organiser's own policy, and --gsi again when the plan writes GSI files
acb undo    [--list | --id n]        put back what restores changed, newest first
acb undo    --set-aside n            move a record aside, so older ones go ahead: one that cannot be read,
                                     or (asked, or --yes) one holding only parts whose Steam, account or
                                     CS2 install is gone, or
                                     an administrator's record of NVIDIA settings the driver will not take
acb backup  [options] -o <archive>   make a backup without DemoViewer; --self-contained carries acb in it
```

Shared options: `--only`, `--account`, `--steam-path`, `--policy`, `--gsi`, `--yes`, `--json`, `--report f`.
A `gamestate_integration_*.cfg` sends live game data to the address it names, for every account on the PC, so
capture carries one only with `--gsi`, and a restore writes one only with `--gsi` too; either way the plan and the
report name each such file. Capture records every file by its SHA-256, so a restore refuses a part whose record
names a file any other way (an undo record's `absent`, which deletes, is honoured only by `acb undo`).
`acb restore ... --dry-run` is `plan` with restore's options, so adding it to a working restore line only stops the
changes; with `--plan` it checks the reviewed plan against the PC and applies nothing (and `acb undo --dry-run` says
what an undo would put back). `--elevate` lets a headless run ask Windows for an administrator. Without a terminal,
nothing changes unless `--yes` is given.

What an archive says (the tool's name, labels, notes, file names, values) is printed with control characters, line
and paragraph separators and text-reordering marks escaped (`\r`, `\x1b`), so a hand-edited backup cannot hide or
rewrite a line of the plan a person reads before agreeing; the JSON report keeps the text, escaped by JSON. A
restore refuses launch options that hold a control character other than a tab, which Steam's own window cannot
enter (a tab is printed as `\t`); `acb backup` says so when it copies such launch options. A part's
outcome is one of a fixed set (done, skipped, deferred, blocked, failed, admin, left), each mapped to an exit
code; any other throws where it is made rather than passing as done.

A plan's JSON report is the plan file (`acb plan b.zip --report plan.json`): it names the `acb` version that made it,
the archive by path and manifest digest, the options and the policy's digest, and a fingerprint over every
part's planned outcome and the SHA-256 of every target the restore would replace (the files, `localconfig.vdf`
for launch options, and each NVIDIA setting's current value on the CS2 profile). The first, unelevated run previews the
administrator's sections too, since reading the driver's settings needs no administrator. `restore --plan`
refuses a plan from another `acb` version for that reason first (what each part says, which the fingerprint
covers, may be worded differently there), then recomputes the plan and refuses if any of it differs. A plan made
from an administrator's prompt marks the parts written through the Steam folder as left, and one made without
writes them, so applying it at the other kind of prompt is
refused for that reason, before the fingerprint is compared. A plan file is not signed, and anyone can make one with
other options, so what would widen a restore is said again on the command line: a plan that writes game state
integration files needs `--gsi`, and `--policy` pins the organiser's own policy, refusing a plan made under
another policy or none. The header names the policy and its digest, and `--gsi`, before anything is asked.
Administrator's records are named `admin:<n>` in `acb undo`.

Exit codes: `0` done, nothing left; `1` refused, nothing changed (verification, platform, policy, a record that
could not be claimed, no consent); `2` done with parts left for later (CS2 running, administrator needed, a target
that cannot be written, a waiting undo, a record set aside in an undo that put back the rest); `3` a part failed
and was rolled back; `4` undo records need attention (a rollback that failed, a record that cannot be read, an undo
store that cannot be reached, an administrator's run that started and ended without a report, which may have
changed its parts whatever its exit code, or a driver part given up on after it may have started changing the PC:
these are reported as unknown, never as left or refused); `64` usage.
`1` holds for the whole run: an administrator's run that refuses after the player's sections changed the PC
leaves its sections, and the run exits `2`.

### The screens

`acb` with no command at a terminal opens its screens, built with [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui)
2.5, which publishes under Native AOT on `win-x64` and `linux-x64` with no trim warnings.

**Home** says what `acb` sees on this PC (the Steam folder, the accounts with CS2 settings and which signed in
last, whether CS2 or Steam is running, how many restores can be put back) and offers the three tasks: back up
this PC, restore a backup, put this PC back. Reading it changes nothing.

Each task runs on one **task screen**: its steps down the left, with the steps done ticked and the current one
marked, and under them a short summary of what is picked (the backup, its level and account; the parts to
save). The step's body is on the right, with Back and Next under it and anything that stops Next said above
them, and the keys along the bottom. Esc goes back a step, and from the first step home; Ctrl+Q quits. Once a
task has changed the PC or written a file there is no going back past that step. Files are picked in a file
dialog or typed, and a passphrase is asked for in a box only when it is needed.

- **Back up**: **What** lists every part, ticked when this PC has it (found by capturing into a temp folder that
  is then deleted), with what it would save, and unticked with the reason when it does not; game state
  integration files are a choice of their own, and the account when several have CS2 settings. **Save** picks
  the file (default: Documents, named by date and time), sealing with a passphrase (typed twice, or six random
  words shown once), and carrying `acb` in the backup when this is a published `acb`. **Done** says what was
  saved and how.
- **Restore**: **Backup** picks the archive, and optionally an organiser's policy and the Steam folder; a sealed
  one asks for its passphrase (three tries). **Choose** is one list of parts and files, each with what a restore
  would do (new here, changed here, already the same, or the settings it would change), ticked to restore; the
  part or file selected shows below it, a file's lines against the backup's (for `cs2_video.txt`, the lines
  naming this PC's GPU and monitor keep this PC's values) and a settings part's table of each value here and
  the value it gets. A `.vcfg` and its `_lastclouded` twin are one choice, and game state integration files are
  a choice of their own, unticked. Choosing only narrows: the session reopens with the choices, so a part the
  rules leave is never offered. **Ready** says what stands in the way (CS2 or Steam running, with "Check again";
  an administrator needed, with "Ask Windows" where Windows can ask; targets that cannot be written; parts an
  administrator's `acb` leaves; the account question when the backup's account has not signed in here) and
  what Restore will apply. **Done** says what changed, what was left and why, and how to undo.
- **Put this PC back**: **Records** lists the player's records newest first (one that cannot be read can be
  set aside there); **Review** dry-runs the undo and shows, per record and part, what it would put back, each
  file as left by the restore, changed since, added by the restore, or already back, with this PC's file
  against what undo puts back; **Choose** ticks records and parts (an unticked record still holds back older
  records' same parts); **Ready** and **Done** follow.

The screens are views over the same types the commands use: restoring over `Restoring/RestoreSession`, putting
back over `acb undo`'s own run, backing up over `BackupBuilder`, so every decision is theirs and the screens word
it, escaping archive text as the commands print it. Each task's steps are tested through its flow
(`Cli/Wizard/BackupFlow`, `RestoreFlow`, `UndoFlow`) and on screen through Terminal.Gui's ANSI driver with
injected keys. On Linux the driver needs a terminal that reports a size, so the suite runs under `script(1)` with
one set by `stty`.

### Elevation (Windows)

Most sections need no administrator. NVIDIA's driver settings do.

- `acb` runs as the invoking user (`asInvoker`) and relaunches itself with UAC only for the sections that need
  it. The prompt shows the signed publisher, so an organiser can trust it before typing a password.
- Before anything is offered, `acb` works out whether Windows would ask at all: already elevated; an
  administrator's split token (Windows asks to confirm); a standard user (Windows asks for an administrator's name
  and password); or a PC where the prompt cannot work (`EnableLUA` 0, or `ConsentPromptBehaviorUser` 0, which
  turns down every standard user's request). In the last case the player is told why and which parts are left,
  and no prompt is shown.
- Windows loads a program's DLLs from its own folder before System32, so the prompt is offered only for a copy of
  `acb.exe` that nobody but Administrators, SYSTEM and TrustedInstaller can change, in a folder nobody else can
  add a file to, under folders nobody else can swap, none of them a link (for a split-token administrator, their
  own account counts too: no one else is asked). Otherwise the person is told to copy `acb` into a folder such as
  `C:\Program Files\acb`, and no prompt is shown. The binary is also linked with `/DEPENDENTLOADFLAG:0x800`, so
  the DLLs it imports (`bcrypt.dll` among them) come from System32 only, and it limits run-time loading to
  System32 as it starts; CI reads the flag back from the published image (`.github/scripts/pe-load-flags.py`).
- The elevated process opens no path the player named. Its command line holds the manifest digest the first run
  showed the person, the sections, the policy's digest and a pipe name; the archive's and the policy's paths are
  there only to be shown and recorded. The archive's and the policy's bytes come over that pipe, which only the
  player and administrators may open, and which the elevated process opens with anonymous impersonation, so the
  pipe's owner cannot act as the administrator. It refuses the bytes unless the digests match, takes no Steam
  folder or account (the driver's settings need neither), applies only the sections that need an administrator,
  and writes its undo records to the administrator-only store. So an organiser's administrator password at the
  prompt works, and never signs in to a share or follows a link a player made.
- `acb` run as an administrator (from an administrator's prompt) applies the administrator's sections and the
  Windows settings itself, but writes nothing through the Steam folder (`cs2`, `cs2game`, `launch`, `cs2exe`),
  restore or undo: any user may change that folder, and a link planted in it would turn an administrator's write
  into one anywhere on the PC. Those parts are left with exit 2 for a run without administrator. Where the
  account has no other kind of prompt (User Account Control off, or the built-in Administrator account, whose
  token is a full one with no split), the parts say so, and that a standard Windows account or User Account
  Control turned on restores them; a way through on such a PC is an open decision.
- The NVIDIA part needs an administrator only where there is something to apply: a preview that finds no NVIDIA
  driver or GPU skips it, and no administrator is offered or asked for. Only NVAPI's "no NVIDIA GPU" and "no
  driver library" answers count as that; any other failure to start (a driver updating or recovering) leaves the
  part for later. The NVIDIA and vibrance parts run on a worker of their own in a restore, an undo, a rollback and
  the preview, as in capture, one at a time, so a driver call that never returns leaves its part after 60
  seconds. A worker given up on before its first change never makes one, and the part is left for later; one
  given up on after it may have started changing the PC is not waited for and dies when `acb` exits. Each driver
  write is recorded before it is made, so the record covers whatever the worker changed; a restore reports that
  part as unknown, stops, and exits 4 (an undo leaves it for the next `acb undo`). A worker cut off while it claims
  its record leaves an empty folder, which changed nothing and is filed, or at worst one the next `acb undo` reports
  as needing attention. The first
  run waits at most 10 minutes for the administrator's run.
- If the player is not an administrator and nobody elevates, those sections are left and reported.
- Power plans and vibrance are machine-wide, but Windows lets a standard user change them, so they run as the
  player and their records stay in the player's store. Only NVIDIA's driver settings need the elevated run.
- Linux v1 needs no root.

### Undo store

| Platform | Per-user sections | Machine-wide sections |
|---|---|---|
| Windows | `%LOCALAPPDATA%\CS2OpenDev\ConfigBackup\undo` | `%ProgramData%\CS2OpenDev\ConfigBackup\undo`, created by the elevated process, owner and ACL Administrators and SYSTEM only, checked on every use from `%ProgramData%` down, listing and setting aside included (any user may make a folder there, so a player's folder or link is refused). Users may list the record folder itself, not read or change it, so `acb undo` can tell that records wait |
| Linux | `$XDG_STATE_HOME/cs2opendev/configbackup/undo` (`~/.local/state/...`), mode 0700 | none in v1 |

- Records are numbered by sequence, claimed atomically, written and flushed before each change
  (write-ahead), and hold the archive's path, digest and level for the report. Numbers count `applied/` and
  `set-aside/` too, so none is reused.
- A file part also keeps, under `wrote`, each file's SHA-256 as the restore left it, saved once its writes are
  done. An undo reads only the part's `files`, so a record without `wrote` (one cut short) is put back all the
  same; the undo screen uses it to show which files changed since the restore.
- A record also holds the Steam folder its restore wrote to, and `acb undo` writes back there unless
  `--steam-path` says otherwise; when that folder is gone, its parts wait and say where they looked. A plain
  `acb undo` therefore never puts one Steam's files into another that the PC would find first. An account's
  parts (`cs2`, `launch`) are forgotten only when the account's folder is gone from the very Steam folder the
  restore wrote to, whose `userdata` is still there (a cafe PC wiped between sessions); looked for anywhere else,
  they wait in the record and say where acb looked. A record that waits on a Steam folder or a `userdata` that is
  gone for good (a wipe that takes all of `userdata`, a Steam moved and its old folder deleted) is let go with
  `acb undo --set-aside n`: for a record that can be read, only when everything it still holds was written
  through a Steam folder and is checked to be stranded there (the recorded Steam folder is gone, or under it the
  account's folder for `cs2` and `launch`, or the CS2 install for `cs2game` and `cs2exe`), after a question (or
  `--yes`) naming what is then never put back. A part that only waits (CS2 running) is refused. With `--dry-run`
  it says what would be set aside and moves nothing. An administrator's
  record that holds only NVIDIA settings can be set aside the same way by an administrator, naming the setting
  ids, for a driver that will never take one back (an update that dropped or retyped the setting).
- Each Windows user has their own store, so the hold-back rule orders one user's records only. Two Windows users
  who both restore the shared cfg folder, the power plan or vibrance, and then undo in the order they restored,
  leave the PC as the first one's restore left it. Ordering them needs a record every user can read and none can
  forge; until then, each user undoes their own restore before another restores (see "Open decisions"). The undo
  screen shows each file a record would put back as left by the restore or changed since, with what it holds now
  against what undo puts back, so a person sees a later change before undoing over it and can leave that part.
- `%LOCALAPPDATA%` and `$XDG_STATE_HOME` are read from the environment, as their names say. Whoever sets acb's
  environment already runs as that user and can write the default store too, so this crosses no boundary, and it
  is how the published binary is run end to end without touching the real store (`.github/scripts/acb-smoke.sh`).
  A restore and its undo started under different values find different stores.
- A restore files its own record away only when it can read it and finds nothing left in it; one it cannot read
  just then stays for `acb undo`.
- A claim that cannot be written is deleted at once and the restore stops before its first change; v1 retried
  on the next number and left a hundred half-made folders on a full disk.
- A record is filed into `applied/` only once every part is back. One the person sets aside goes into
  `set-aside/` and holds back nothing after that; an undo that set one aside exits 2 and says its changes stay.
  An NVIDIA setting the driver will not take back stays in the record for the next `acb undo`, and the part is
  left for later rather than reported done. Older records' NVIDIA parts wait behind it; when the driver never
  will, the part says that an administrator's `acb undo --set-aside` lets them go ahead.
- A claim makes its `backup.vdf` before anything else goes into the record folder, so only an empty folder is a
  claim that changed nothing. A folder that holds anything else without its `backup.vdf` lost its record, and is
  unreadable. A store that does not show up is empty only when the folder above it lists it as missing; one on a
  path that cannot be listed or reached is reported, with exit 4, never as "nothing to undo".
- A restore that copies a Windows power plan template notes that it is doing so before `powercfg` runs. When
  `powercfg` fails or is stopped, a plan that appeared meanwhile, the only one, is recorded as the copy so the
  rollback removes it; an undo of a record that says a copy was being made, with none recorded, says one may be
  left and how to remove it.
- An administrator's record that cannot be read is offered for setting aside when an administrator runs
  `acb undo` at a terminal. The elevated run a player starts asks nothing (its console closes when it ends),
  so it reports the record back and the player's run names it, saying an administrator sets it aside.
- Undo applies newest first. A section of an older record waits while a newer record still holds the same
  section (v1's hold-back rule).
- A machine that resets itself on reboot (Deep Freeze and the like) loses the store, and with it the
  changes, which is consistent.

### Failure policy

| Situation | Interactive | Headless |
|---|---|---|
| Verification fails, platform or format unknown, policy refuses | stop, name the reason | exit 1 |
| CS2 or Steam running | offer to wait or close; default skip those sections | skip, exit 2 |
| Administrator needed | offer UAC; default skip | skip, exit 2 |
| Target read-only, locked or denied | name it; default skip the section | skip, exit 2 |
| A section fails partway | roll that section back from its undo record, report | same, exit 3 |
| Rollback itself fails | stop everything, show the record, keep it | exit 4 |
| An undo record is unreadable | stop; offer retry, set aside (moved to `undo/set-aside`), or leave; default leave | leave, exit 4 |
| An undo record cannot be claimed (disk full) | delete the half-made claim, stop before any change | exit 1 |
| A dry run or `plan` | changes nothing, attributes included | same |

The table's "read-only" is a target this user may not write, or a folder it may not add a file to: every write
first adds a temporary file beside its target, so a cfg folder locked against new files blocks the part in the
plan and the restore alike. An undo entry that only removes a file the restore added asks for the right to remove
it, not to add to its folder, so that lock does not keep a player's new `autoexec.cfg` for the next customer.
Registry values are checked the same way: each key is opened for writing (or the nearest key above it that
exists) before the first value is written, so a key this user may not change blocks the part in the plan and the
restore alike. A file with only the read-only attribute set (a
common way to stop CS2 resetting `cs2_video.txt`) is written, as in v1, and keeps the attribute. Each row is
one test in `test/CS2OpenDev.ConfigManager.Cli.Tests/FailurePolicyTests.cs`, through `acb`'s own command line;
each rule kept from v1, and each v1 bug that must not come back, is one in `V1LessonsTests.cs` there.

## Platforms and sections

Archives are platform-specific. Sections exist per platform; the manifest's `platform` decides which set
applies.

| Section | Windows x64 | Linux x64 |
|---|---|---|
| `cs2` account settings | `Steam\userdata\<id>\730\local\cfg` | same layout under the Linux Steam root |
| `cs2game` player cfgs | `...\game\csgo\cfg` | same |
| `launch` options | `localconfig.vdf` | same |
| `input` accessibility hotkeys | yes | no |
| `gamebar` | yes | no |
| `cs2exe` GPU preference, fullscreen optimizations | yes | no |
| `power` plan | yes | no (power profiles are desktop-specific; later) |
| `nvidia` 3D settings | NVAPI DRS, CS2 profile | the driver's application profiles: see below |
| `vibrance` | NVAPI DVC | later (`nvidia-settings`, X11 only) |

On Linux, `nvidia` is the driver's application profiles (README, "Application Profiles"): JSON with `#` comments
and hex or octal numbers, read from `~/.nv/nvidia-application-profiles-rc` (a file, or a folder of files) and
then `~/.nv/nvidia-application-profiles-rc.d`. Capture keeps only the rules whose pattern plainly names the `cs2`
process (the name as a string, a `procname` or `commname` match, an empty pattern that matches everything), resolves
their profiles wherever they are defined, takes the first value of each key as the driver does, and carries only
the keys that change how CS2 renders or paces frames (not paths, device choices or keys that loosen checks). A rule
with a compound or library pattern is said to be left. A restore never edits the player's files: it writes one
file of its own, `~/.nv/nvidia-application-profiles-rc.d/50-acb-cs2`, recorded first, and `acb undo` removes it or
puts back the copy it replaced. The driver reads the rc file before the `.d` folder, so where the target's own rc
file gives `cs2` a setting, that value wins, and the restore says so. A `.d` that is a file blocks the part.

Linux Steam roots, in order: `~/.steam/steam` (a link), `~/.local/share/Steam`, and Flatpak's
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam`. A Linux CS2 runs natively; its `cfg` layout matches
Windows.

## Testing on real systems

Unit tests prove acb's decisions against fakes (a fake Steam folder, a fake machine). What they cannot prove is that
the published binary behaves on a real system, so the binary also runs on "player PCs" (`test/system/`), each thrown
away after the run, in layers:

1. **Fakes**, every push: every section's restore, rollback and undo, and every rule.
2. **A Linux container**, every push: Steam found by itself, the default store, CS2 running, read-only folders, the
   NVIDIA application profile.
3. **A Windows container** (Server Core, process isolation on `windows-2022`), every push: Steam found through the
   registry, standard-user restores, real NTFS denials and locks, the administrator's rule for the Steam folder.
4. **The Windows runner VM itself**, nightly and on pushes to main: the Game Bar values and the power plan restored
   and undone through the real registry and `powercfg`.
5. **A VM we control**, before a release: User Account Control on and set to elevate without asking, for the
   administrator's run, its pipe and the `%ProgramData%` store end to end; a second snapshot with User Account
   Control off for the open decision about such PCs. The prompt for an administrator's password needs a person.
6. **A spare PC with an NVIDIA GPU and a monitor**, before a release, by checklist: NVIDIA settings and vibrance on a
   real driver, and whether restored settings change anything in CS2.

Steam is mimicked by a fixture script run inside each machine, not baked into an image, so the same script serves
every layer and each run starts clean. Layers 2 to 4 run in `.github/workflows/ci.yml`; layers 5 and 6 are not
built yet.

## Distribution and code signing

- `release.yml` publishes `acb` on a `v*` tag, after a maintainer approves the `release-signing` environment:
  `acb-win-x64.exe` and `acb-linux-x64`, each built Native AOT, tested and smoke-tested alone in an empty folder,
  with `SHA256SUMS` and a GitHub build provenance attestation for every file (`gh attestation verify <file>
  --repo CS2OpenDev/CS2OpenDev-ConfigManager --signer-workflow CS2OpenDev/CS2OpenDev-ConfigManager/.github/workflows/release.yml
  --source-ref refs/tags/<tag>`). The release is created only after that check passes on every file in the
  workflow itself. Run by hand it is a dry run that keeps the binaries as workflow artifacts and attests nothing. The
  tag must name the version `version.json` gives the commit.
- Attestations are stored by GitHub for public repositories on every plan; a private repository needs Enterprise
  Cloud, so the repository is public.
- `acb.exe` is not Authenticode-signed yet. Windows code signing comes from the stack-wide work (DemoViewer.NET's
  `docs/code-signing/plan.md`), which is waiting on a publisher identity; when it lands, `release.yml` signs
  `acb.exe` before checksumming it. Linux has no OS signing to target, so there the attestation and `SHA256SUMS`
  are the signature, as for DemoViewer.NET's Linux build.
- `acb` reads every earlier archive format it shipped with; `format` is append-only.

### Self-contained archives

`acb backup --self-contained` carries the running `acb` in the backup as `restorer/acb.exe` (Windows) or
`restorer/acb` (Linux), listed in the manifest like any entry, so a PC with nothing installed can restore it. Only
a published `acb` is carried (named `acb` or as a release names it); a test host or `dotnet` is refused. The entry
may be up to 64 MB, against 16 MB for a settings file, and the reader refuses any other name under `restorer/`,
including the other platform's.

Running the restorer is trusted through where it was built, not through anything the backup says about itself.
Until `acb.exe` is code-signed that is its build provenance attestation: an attestation is looked up by SHA-256, so
`gh attestation verify restorer/acb.exe` on the unpacked restorer passes only for a released `acb`. A separately
installed `acb` checking such a backup (`verify`, `show`, `plan`, `restore`, the screens) prints the restorer's
SHA-256 and says whether it is byte for byte that `acb`; otherwise the person checks it against the `SHA256SUMS`
of the release it should be, or with the attestation. No offline list of released builds is kept: a
build cannot know the releases after it.

`acb` still never reads an unpacked backup. A restorer has to be unpacked to run, so it looks for the zip it came
from where Windows' Extract All and `unzip x.zip -d x` put the folder: `dir/name/restorer/acb` proposes
`dir/name.zip`, which the restore screen offers first and opens and checks like any other backup. If the zip is not there
the restore screen asks for it as usual. The Linux entry is stored with mode 755, so `unzip` unpacks it runnable. Windows
Explorer's "run from inside the zip" unpacks only the one program into `%TEMP%\Temp1_name.zip\restorer\`; from
that folder name the restorer looks for `name.zip` in Documents, Desktop, Downloads and the home folder, where a
downloaded or saved backup is, and the restore screen offers it as above. Elsewhere it is not found, so the README
inside the backup still says to use Extract All.

### The easy path

The screens are built for a person who reads nothing and presses Enter. Each default is the usual answer, and
none of them is riskier than asking, as every one still ends at a step that says what will change:

- `acb <backup.zip>` (a zip dropped on `acb.exe`, or opened with it) opens the screens on restoring that backup.
  Without a terminal it says the `verify` and `restore` commands instead.
- Backing up ticks every part this PC has and saves unsealed to Documents, named by the date and time.
- Restoring offers a backup before the person picks one: the one a carried restorer came in, else the newest
  `AdvancedConfigBackup-*.zip` in Documents, Desktop, Downloads or the home folder, saying which. A folder picked
  instead of a zip means the zip beside it of the same name, as a carried restorer's does; with none, the refusal
  says to pick the zip. The browse box starts in Documents.
- "Ask Windows for an administrator" starts ticked wherever Windows can ask, on restore and on undo. Windows' own
  box is then the question; no there leaves only those settings, as before.
- Done says to pick "Put this PC back" on the first screen, not only `acb undo`.
- Double-clicked, Windows closes `acb`'s console window the moment it ends. When `acb` owns its window (the only
  process on its console) and printed words instead of showing its screens, it waits for Enter first. An elevated
  run never waits.

The DemoViewer.NET plugin does not make self-contained backups: it would have to ship `acb` binaries for both
platforms, and it cannot load until DemoViewer.NET can load it (see "Loading the plugin in DemoViewer.NET").

## Where it stands

- Built: the `hash` and `seal` levels, every command, the screens, Windows and Linux sections, self-contained
  backups and the release workflow. The DemoViewer.NET plugin writes v2 archives; the v1 PowerShell restorer is gone.
- `identity`: checking and signing are built and tested against a stand-in service. It is off until the CS2OpenDev
  service exists and the root keys are made, and it gets a security review before it is turned on.
- Windows code signing waits on the publisher identity.

### Libraries

Terminal.Gui 2.5.0 for the screens and Konscious.Security.Cryptography.Argon2 1.3.1 (managed, no native code) for
`seal` both publish Native AOT with no trim or AOT warnings, and Argon2id matches RFC 9106 section 5.3's test
vector. Terminal.Gui's `IDriver.ToString()` returns the screen as text after layout, which the screens' tests
assert on without a real terminal. Build machines need the platform linker: on Windows the MSVC tools, with
`vswhere.exe` on `PATH`; on Linux `clang`, or `gcc` with `-p:CppCompilerAndLinker=gcc`.

### Known gaps

Not tested, because it needs a UAC prompt or changes the machine: the elevated run itself, its pipe (the input it
reads, the report it sends, and the pipe's access list), and the administrator's store made under the real
`%ProgramData%`. Their parts are tested apart:

- the arguments and bytes the first run passes (restore and undo; no Steam folder, account or path to open), and
  the input's encoding (a cut-off or oversized one refused);
- the elevated run's refusals (no input over the pipe, no administrator, a changed archive, a changed policy, a
  section that needs no administrator, a store folder that fails the check), for restore, undo, `--list` and
  `--set-aside`;
- how a refusal after the player's changes is reported, the store's ACL check over security descriptors, and the
  records an elevated undo reports back (unreadable, or filed with nothing to put back).

Tests never let the engine write to a real driver, so the elevated NVIDIA undo is tested with records that hold
nothing or cannot be read; an NVIDIA setting the driver refuses to take back, and a power plan copy recorded after
`powercfg` failed, are not exercised. A driver that stops answering is tested in the plan's preview through a
stand-in for starting NVAPI (the part is left, nothing changed, the run ends); a driver part given up on after it
started writing is tested only as the gate's logic. The restricted-token way through for a PC with User Account
Control off is not built. The machine-wide sections are restored for real only on the CI runner (layer 4 of
"Testing on real systems") and, by checklist, on a spare PC.

## Open decisions

- The service that runs `identity` signing: the planned shared CS2OpenDev web service, built to `docs/identity-service.md`.
- How DemoViewer.NET loads the plugin: a declared first-party pack, or general installed extensions (see
  "Loading the plugin in DemoViewer.NET").
- The restorer's command name (`acb` for now).
- A PC where the account runs everything as an administrator (User Account Control off, a shared admin account
  on a cafe image): the parts written through the Steam folder are left there with advice that can be followed.
  A way through is a child `acb` started with a restricted token (Administrators deny-only, privileges removed),
  so a link planted in the Steam folder resolves with a standard user's rights. It needs that second process
  protocol and checking on a PC with User Account Control off; it goes with the spare-PC run of the machine-wide
  sections.
- Undo across Windows users of the shared sections (`cs2game`, `power`, `vibrance`): the undo screen shows a
  file changed since its restore, with what undo would put back, and lets the person leave that part; the
  headless `acb undo` does not, and nothing orders two users' records. A store every user can read and none can
  change would order them; it is still open, as is showing a changed power plan or vibrance level in the undo
  screen.
