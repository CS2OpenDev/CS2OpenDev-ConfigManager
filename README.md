# CS2OpenDev ConfigManager

Advanced Config Backup: save a player's CS2 settings, and the Windows and NVIDIA settings that change how
CS2 feels, into one archive, then restore them on another PC and put that PC back afterwards. For players who
move between PCs at internet cafes, LAN events and tournaments, and for the organisers who set those PCs up.

It is a standalone tool first. A first-party [DemoViewer.NET](https://github.com/sid2934/DemoViewer.NET) plugin,
built from this repository, makes backups from inside the app.

> Status: early development. The headless restorer `acb` checks, shows, plans, restores and undoes backups,
> and makes them; the step-by-step wizard comes next. See [docs/design.md](docs/design.md) for the design and
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
read, and any change to them after they are made is detected and refused. A backup restores only on the
platform it was made on (Windows x64 or Linux x64). [docs/v1-reference.md](docs/v1-reference.md) has the full
list of what is saved, what is left out on purpose, and why.

## Using acb

```
acb backup -o backup.zip                 save this PC's settings
acb verify backup.zip                    check it: who made it, when, and that nothing was changed
acb plan backup.zip                      what a restore would change, changing nothing
acb restore backup.zip                   restore it, recording what it replaces
acb undo                                 put the PC back as it was
```

`acb` with no command lists every option. It runs as the person who started it; on Windows only the NVIDIA
settings need an administrator, and `acb` says so up front and asks before Windows does. An organiser can pass a
policy file (`--policy`) that only narrows what is restored.

## Layout

```
src/CS2OpenDev.ConfigManager.Core/            capture and restore engine, rules, archive format, undo store
src/CS2OpenDev.ConfigManager.Cli/             acb, the restorer (Native AOT, win-x64 and linux-x64)
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

MIT. See [LICENSE](LICENSE).
