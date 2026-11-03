# CS2OpenDev ConfigManager

Advanced Config Backup: save a player's CS2 settings, and the Windows and NVIDIA settings that change how
CS2 feels, into one archive, then restore them on another PC and put that PC back afterwards. For players who
move between PCs at internet cafes, LAN events and tournaments, and for the organisers who set those PCs up.

It is a standalone tool first. A first-party [DemoViewer.NET](https://github.com/sid2934/DemoViewer.NET) plugin,
built from this repository, makes backups from inside the app.

> Status: early development. The restorer `acb` checks, shows, plans, restores and undoes backups, and makes
> them, headless or on its own screens. See [docs/design.md](docs/design.md) for the design and
> plan.

## What it saves

| Part | What |
|---|---|
| `cs2` | CS2 settings, binds, crosshair and video for one Steam account |
| `cs2game` | The player's own cfg files (`autoexec.cfg`, practice cfgs) |
| `launch` | CS2 launch options |
| `input`, `gamebar`, `cs2exe`, `power` | Windows settings that matter to CS2 (Windows only) |
| `nvidia`, `vibrance` | NVIDIA 3D settings for CS2's profile, digital vibrance |

Passwords and server tokens are removed before anything is written. Archives are plain text you can open and
read. A file that does not match the archive's manifest is detected and the archive refused; an unsigned
(`hash` level) archive proves only that, since anyone can edit it and rewrite its manifest. A backup sealed with
a passphrase (`seal` level, `acb backup --seal`, or the plugin's "Seal the backup" box) opens only with that
passphrase, which shows it was made by someone who knows it and is unchanged since. A backup restores
only on the platform it was made on (Windows x64 or Linux x64). [docs/v1-reference.md](docs/v1-reference.md) has the full
list of what is saved, what is left out on purpose, and why.

## Using acb

```
acb                                      the screens: back up, restore, or put this PC back
acb backup -o backup.zip                 save this PC's settings
acb backup -o backup.zip --seal          the same, sealed with a passphrase (Enter alone makes six random words)
acb backup -o backup.zip --sign          signed for your Steam account (needs the CS2OpenDev service; not running yet)
acb verify backup.zip                    check it: its trust level, when it was made, that its files match
acb plan backup.zip                      what a restore would change, changing nothing
acb restore backup.zip                   restore it, recording what it replaces
acb undo                                 put the PC back as it was
```

`acb` with no command at a terminal opens its screens: home says what it sees on this PC, and from there you
back up, restore or put the PC back, step by step. Restoring shows each file a restore would change against
this PC's copy and lets you untick parts and files; backing up shows which parts this PC has. `acb help` lists every option.

The screens pick the usual answer for you, so pressing Enter through them works:

- On Windows, double-click `acb.exe` to open them, or drop a backup onto `acb.exe` to go straight to restoring it.
- Backing up saves every part this PC has, unsealed, to Documents under the date and time.
- Restoring offers the newest backup in Documents, Desktop, Downloads or your home folder. A picked folder that
  was unpacked from a backup means the zip beside it.
- When the NVIDIA settings need an administrator, "Ask Windows" starts ticked, so Windows' own box asks. Saying
  no there leaves only those settings.
- If `acb` was double-clicked and ends with a message, its window waits for Enter instead of closing.

Game state integration files (`gamestate_integration_*.cfg`) are restored only with `--gsi`, since they send
live game data to the address they name.

`acb undo` writes back into the Steam folder each restore wrote to. If that folder is gone for good,
`acb undo --set-aside <record>` lets the record go; an administrator can do the same for NVIDIA settings a
driver will no longer take back.

`acb` runs as the person who started it. On Windows only the NVIDIA settings need an administrator: `acb` says so
up front and asks before Windows does, and on a PC with no NVIDIA driver that part is skipped. Windows is asked
only for a copy of `acb.exe` in a folder nobody but administrators can change (such as `C:\Program Files\acb`).
`acb` run as an administrator writes nothing into the Steam folder, so run it without administrator for those
parts (on a PC where User Account Control is off, from a standard Windows account).

An organiser can pass a policy file (`--policy`) that only narrows what is restored. Players can pass the
event's policy to `acb backup --policy` too, which refuses up front a backup that policy would refuse.

A signed backup whose signing key was later revoked is still shown, with a warning that its signature no longer
proves who made it and that its files still match (so it is unchanged since it was made). `acb` asks before using
it, or takes `--accept-revoked`; under a policy that needs a signature, only an administrator's `--accept-revoked`
overrides the policy's refusal.

`acb plan backup.zip --report plan.json` saves a plan for `acb restore --plan plan.json`, which the same version of `acb` applies only
while the PC still matches it (and with the same policy, given again); add `--dry-run` to check that without
applying it.

A sealed backup asks for its passphrase at the terminal, or reads it from `--passphrase-file` (its first line;
`-` for standard input). A passphrase needs at least 12 characters, and anyone holding the backup can try
passphrases against it, so a generated one is safest. It is taken exactly as typed, so an accented letter typed
another way on another PC does not open the seal.

`acb backup -o backup.zip --self-contained` also carries `acb` itself in the backup (`restorer/acb.exe`, or
`restorer/acb` on Linux), for a PC with nothing installed. Unpack the whole backup beside the zip (Extract All on
Windows, `unzip backup.zip -d backup` on Linux) and run the restorer from that folder: it finds the zip and checks
it as usual. On Windows you can also open the zip and double-click `restorer\acb.exe` in it: Windows runs a copy
from a temp folder, and `acb` looks for the zip by name in Documents, Desktop, Downloads and your home folder. Checking such a backup with another `acb` says whether its restorer is the same program.

## Downloads

Each release carries `acb-win-x64.exe`, `acb-linux-x64` and `SHA256SUMS`, and every file has a build provenance
attestation from the release workflow. To check a download:

```
sha256sum -c --ignore-missing SHA256SUMS
gh attestation verify acb-win-x64.exe --repo <owner>/CS2OpenDev-ConfigManager
```

`acb-win-x64.exe` is not code-signed yet, so Windows warns about an unknown publisher until it is.

## Layout

```
src/CS2OpenDev.ConfigManager.Core/            capture and restore engine, rules, archive format, undo store
src/CS2OpenDev.ConfigManager.Cli/             acb, the restorer, with its screens (Native AOT, win-x64 and linux-x64)
plugin/CS2OpenDev.ConfigManager.DemoViewer/   the DemoViewer.NET plugin
test/                                         TUnit suites
docs/                                         design of record and reference
```

## Building

Needs the .NET 10 SDK (`global.json`).

```
dotnet tool restore
dotnet build CS2OpenDev.ConfigManager.slnx
dotnet run --project test/CS2OpenDev.ConfigManager.Core.Tests
dotnet run --project test/CS2OpenDev.ConfigManager.Cli.Tests
dotnet run --project test/CS2OpenDev.ConfigManager.DemoViewer.Tests
dotnet publish src/CS2OpenDev.ConfigManager.Cli -c Release -r win-x64    (or linux-x64)
```

Publishing `acb` needs the platform linker: the MSVC build tools on Windows (with `vswhere.exe` on `PATH`), and
`clang` on Linux, or `gcc` with `-p:CppCompilerAndLinker=gcc`.

Tests never touch the machine's own Steam, registry, display or driver settings: every restore writes into a
fake Steam folder and an undo store in temp, and the administrator's prompt is a stand-in.

## License

MIT. See [LICENSE](LICENSE). Third-party parts, among them the EFF word list generated passphrases are drawn
from, are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
