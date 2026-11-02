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
src/CS2OpenDev.ConfigManager.Cli/           `acb`: TUI wizard and headless commands (Native AOT)
plugin/CS2OpenDev.ConfigManager.DemoViewer/ the DemoViewer.NET plugin: the Settings page that makes backups
test/<project>.Tests/                       one TUnit project per component
```

- **Core** targets `net10.0`, is marked `IsAotCompatible`, and references nothing from DemoViewer.NET or
  Avalonia. The C# 5 and ASCII rules of v1's kit went with PowerShell; the kit keeps its style until it is
  reworked. `Restoring/` holds what the restorer adds around the engine: the archive as the engine's source,
  the undo store and its runner, the organiser's policy, and the administrator's store ACL.
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
- refuses `format` values it does not know and any `platform` other than the one it runs on.

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

### `identity`: signed by a Steam account

The model is Sigstore's (sign in with an identity, a service vouches for it, check offline) with Steam as
the identity provider.

1. The player signs in to the CS2OpenDev service with Steam (OpenID 2.0, through a loopback browser flow).
2. DemoViewer sends the manifest's SHA-256, the platform and the tool version. The configuration itself
   never leaves the PC.
3. The service builds the statement with predicate `{ level: "identity", steamId64, signedAt }`, signs it
   with ECDSA P-256 using a key held in an HSM or key vault, and returns the envelope with the signing
   key's delegation (below). It records `(steamId64, digest, time)` in an append-only log.
4. The restorer verifies offline against the root keys built into it.

Key management follows [TUF](https://theupdateframework.io/)'s split, cut down:

- **Root keys** (offline, two of three to sign) are compiled into `acb` and DemoViewer.
- Root keys sign a **delegation**: the current signing keys, each with a validity window. The envelope
  carries the delegation that was current when it was signed, so checking needs no network.
- A signature counts if its key was delegated and in its window at `signedAt`. A compromised key is
  revoked by a new delegation that lists it as revoked from a time, which ships with the next `acb`
  release and is fetched opportunistically when online.
- ECDSA P-256 is in the .NET base library on both platforms and works under Native AOT. A post-quantum
  signature (ML-DSA, in .NET 10 where the OS supports it) can be added beside it later as a second
  signature in the same envelope.

### Organiser policy

An organiser restoring many players' backups passes a policy file. A policy can only narrow the built-in
rules, never widen them.

```json
{
  "minimumLevel": "identity",
  "steamIds": ["76561198029679050", "76561197960287930"],
  "sections": { "allow": ["cs2", "cs2game", "launch", "nvidia"] },
  "nvidia": { "deny": ["0x10835002"] },
  "requireCs2Closed": true
}
```

Every field takes away: a higher level, fewer signers, fewer sections, fewer NVIDIA settings, and with
`requireCs2Closed` a running CS2 refuses the whole restore instead of leaving its sections. A field the restorer
does not know, a misspelt section, a malformed id, comments or trailing commas refuse the file, so a policy is
never half-applied. `steamIds` is checked against a signature, never the manifest's own account, so until
`identity` (phase 7) a policy that names Steam IDs refuses every backup.

## The restorer

### Rules are compiled in

Per platform, `acb` carries the allowlist: which paths under which roots, which registry values with which
kinds, which NVIDIA setting IDs with which types and sizes, which power plan operations. v1's capture-time
and restore-time rules (`IsAccountSetting`, `IsPlayerCfg`, `IsRestorablePath`, the registry and NVIDIA
allowlists, hardware-key merges) move into Core as one rule set used by both capture and restore.

Whether a run is a restore or an undo comes from the command (`acb restore`, `acb undo`), never from the
archive. An undo record is never an archive; it is read only from the restorer's own store. The engine takes the
mode as a parameter and reads its input through a source: a verified archive's bytes in memory, or an undo
record's folder. The bytes it checks against `capture.vdf` are the bytes it writes; nothing is unpacked.

### Commands

```
acb                                  wizard (TUI)
acb verify  <archive> [--policy f]   check only; prints level, signer, sections, entries
acb show    <archive> [--section s]  list or print captured files and values
acb plan    <archive> [options]      what a restore would change, as text or --json
acb restore <archive> [options]      restore (asks unless --yes)
acb restore --plan plan.json         apply exactly a reviewed plan, refusing if the PC changed since
acb undo    [--list | --id n]        put back what restores changed, newest first
acb undo    --set-aside n            move an unreadable record aside, so older ones go ahead
acb backup  [options] -o <archive>   make a backup without DemoViewer
```

Shared options: `--only`, `--account`, `--steam-path`, `--policy`, `--yes`, `--json`, `--report f`.
`--dry-run` is `plan` (and `acb undo --dry-run` says what an undo would put back). `--elevate` lets a headless
run ask Windows for an administrator. Without a terminal, nothing changes unless `--yes` is given.

A plan's JSON report is the plan file: it names the archive by path and manifest digest, the options and the
policy's digest, and a fingerprint over every part's planned outcome and the SHA-256 of every target the
restore would replace (the files, and `localconfig.vdf` for launch options). `restore --plan` recomputes the plan
and refuses if any of it differs. Administrator's records are named `admin:<n>` in `acb undo`.

Exit codes: `0` done, nothing left; `1` refused, nothing changed (verification, platform, policy, a record that
could not be claimed, no consent); `2` done with parts left for later (CS2 running, administrator needed, a target
that cannot be written, a waiting undo); `3` a part failed and was rolled back; `4` undo records need attention
(a rollback that failed, a record that cannot be read); `64` usage.

### The wizard

Built with [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) 2.5, which publishes under Native AOT on
`win-x64` and `linux-x64` with no trim warnings.

1. **Open**: pick the archive.
2. **Check**: level, signer (Steam ID and name when known), creation date, platform, tool version. A failed
   check stops here and names what failed.
3. **Review**: each section with its files and values. Text files show a diff of the target's current
   file against the backup's; settings show a table of current and new values.
4. **Choose**: tick sections and files.
5. **Pre-flight**: CS2 or Steam running, administrator needed, read-only targets, account not signed in.
6. **Apply**: progress per section.
7. **Done**: what changed, what was left and why, how to undo.

`acb undo` has the same shape: list records, review, choose, apply.

### Elevation (Windows)

Most sections need no administrator. NVIDIA's driver settings do.

- `acb` runs as the invoking user (`asInvoker`) and relaunches itself with UAC only for the sections that need
  it. The prompt shows the signed publisher, so an organiser can trust it before typing a password.
- Before anything is offered, `acb` works out whether Windows would ask at all: already elevated; an
  administrator's split token (Windows asks to confirm); a standard user (Windows asks for an administrator's name
  and password); or a PC where the prompt cannot work (`EnableLUA` 0, or `ConsentPromptBehaviorUser` 0, which
  turns down every standard user's request). In the last case the player is told why and which parts are left,
  and no prompt is shown.
- The elevated process gets only a command line: the archive's path, the manifest digest the first run showed
  the person, the sections, the policy's path and digest, and a pipe name. It reads the archive again and
  refuses it unless the digest matches, applies only the sections that need an administrator, and writes its
  undo records to the administrator-only store. It trusts no folder the player owns, so an organiser's
  administrator password at the prompt works. Its report comes back over a named pipe it
  opens with anonymous impersonation, so the pipe's owner cannot act as the administrator.
- If the player is not an administrator and nobody elevates, those sections are left and reported.
- Power plans and vibrance are machine-wide, but Windows lets a standard user change them, so they run as the
  player and their records stay in the player's store. Only NVIDIA's driver settings need the elevated run.
- Linux v1 needs no root.

### Undo store

| Platform | Per-user sections | Machine-wide sections |
|---|---|---|
| Windows | `%LOCALAPPDATA%\CS2OpenDev\ConfigBackup\undo` | `%ProgramData%\CS2OpenDev\ConfigBackup\undo`, created by the elevated process, owner and ACL Administrators and SYSTEM only, checked on every use from `%ProgramData%` down (any user may make a folder there, so a player's folder or link is refused). Users may list the record folder itself, not read or change it, so `acb undo` can tell that records wait |
| Linux | `$XDG_STATE_HOME/cs2opendev/configbackup/undo` (`~/.local/state/...`), mode 0700 | none in v1 |

- Records are numbered by sequence, claimed atomically, written and flushed before each change
  (write-ahead), and hold the archive's path, digest and level for the report. Numbers count `applied/` and
  `set-aside/` too, so none is reused.
- A claim that cannot be written is deleted at once and the restore stops before its first change; v1 retried
  on the next number and left a hundred half-made folders on a full disk.
- A record is filed into `applied/` only once every part is back. One the person sets aside goes into
  `set-aside/` and holds back nothing after that.
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

The table's "read-only" is a target this user may not write. A file with only the read-only attribute set (a
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
| `nvidia` 3D settings | NVAPI DRS, CS2 profile | `~/.nv/nvidia-application-profiles-rc`: only rules matching CS2 and the profiles they use, merged so the target's other rules stay |
| `vibrance` | NVAPI DVC | later (`nvidia-settings`, X11 only) |

Linux Steam roots, in order: `~/.steam/steam` (a link), `~/.local/share/Steam`, and Flatpak's
`~/.var/app/com.valvesoftware.Steam/.local/share/Steam`. A Linux CS2 runs natively; its `cfg` layout matches
Windows.

## Distribution and code signing

- `acb` is signed through the stack-wide code-signing work: Authenticode on Windows, a detached signature
  plus `SHA256SUMS` on Linux.
- A self-contained archive carries `restorer/acb[.exe]` for its own platform, listed in the manifest like
  any entry. Running it is trusted through its code signature (the publisher Windows shows), not through
  anything it says about itself. A separately installed `acb` checking such an archive also reports
  whether the bundled restorer matches a released build.
- `acb` reads every earlier archive format it shipped with; `format` is append-only.

## Plan

| Phase | Work | Status |
|---|---|---|
| 0 | Spike: Terminal.Gui 2.5 and an Argon2id library under Native AOT on `win-x64` and `linux-x64` | done |
| 1 | Core: move the kit into a library; one rule set; archive v2 writer and strict reader; `hash` level | done |
| 2 | `acb` headless: `verify`, `show`, `plan`, `restore`, `undo`, `backup`; undo store; failure policy; Windows elevation; policy files. The extension switches to v2; delete `Restore.ps1` and the `.cmd` files | done |
| 3 | Wizard | each step driven in tests through Terminal.Gui's test driver |
| 4 | Linux sections and Steam roots | tests on Linux, `linux-x64` binary |
| 5 | `seal` | known-answer and tamper tests |
| 6 | Self-contained archives, packaging, signing integration | signed builds from CI |
| 7 | `identity` (needs the service) | end-to-end against a staging service; a security review |

### Libraries

Terminal.Gui 2.5.0 for the wizard and Konscious.Security.Cryptography.Argon2 1.3.1 (managed, no native code) for
`seal` both publish Native AOT with no trim or AOT warnings, and Argon2id matches RFC 9106 section 5.3's test
vector. Terminal.Gui's `IDriver.ToString()` returns the screen as text after layout, which the wizard's tests
can assert on without a real terminal; on Linux the terminal must report a size, so tests set one with `stty`.
Build machines need the platform linker: on Windows the MSVC tools, with `vswhere.exe` on `PATH`; on Linux
`clang`, or `gcc` with `-p:CppCompilerAndLinker=gcc`.

### Known gaps

Not tested, because it needs a UAC prompt or changes the machine: the elevated run itself, its pipe, and the
administrator's store made under the real `%ProgramData%`. Their parts are tested apart: the arguments the first
run passes, the elevated run's refusals (no administrator, a changed archive, a changed policy, a section that
needs no administrator, a store folder that fails the check), and the store's ACL check over security
descriptors. The machine-wide sections are still restored nowhere (see [v1-reference.md](v1-reference.md),
"Verified, and not"); the plan is disposable CI runners or a spare PC.

## Open decisions

- The service that runs `identity` signing. Assumed: the planned shared CS2OpenDev web service.
- How DemoViewer.NET loads the plugin: a declared first-party pack, or general installed extensions (see
  "Loading the plugin in DemoViewer.NET").
- The restorer's command name (`acb` for now).
