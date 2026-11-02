# System tests: acb on player PCs

The TUnit suites prove acb's decisions against fakes. These run the published binary on machines shaped like a
player's PC, where acb finds Steam by itself and writes its real default undo store. Every machine here is thrown
away after the run, which is the only reason the scripts may change it: each one refuses to run unless
`ACB_DISPOSABLE_MACHINE=1`, which only the images and the CI job set. Never set it on your own PC.

| Layer | Where | Runs | Proves |
|---|---|---|---|
| `linux/` | Ubuntu container, non-root `player` | every CI run | Steam found through `~/.steam/steam` and Flatpak's root; restore and undo with the default store at 0700, also when the store was left open; CS2 running leaves its parts; a read-only cfg folder; the NVIDIA application profile written as acb's own file, the player's left |
| `windows/` | Server Core container (process isolation), standard user and ContainerAdministrator | every CI run | Steam found through `HKCU\Software\Valve\Steam\SteamPath`; restore and undo as a standard user; CS2 running; real NTFS deny entries, locked files and the read-only attribute; an administrator's acb writes nothing through the Steam folder. It also reports, without asserting, what the HKCU sections, powercfg and the store's ACL do in a container |
| `windows-vm/` | the GitHub-hosted Windows runner itself | nightly, on demand, pushes to main | the Game Bar registry values and the power plan really restored and undone through the real registry and powercfg |

`fixture/` writes a Steam folder shaped like an installed one, with CS2 and one signed-in account. Nothing in it
comes from Valve; every file holds only the keys acb reads.

What none of these can prove, and what needs a VM we control or a spare PC with an NVIDIA GPU and a monitor: the
UAC prompt, the administrator's run and its pipe, the `%ProgramData%` store end to end, NVIDIA driver settings and
vibrance on a real driver, and whether a restored setting changes anything in CS2. See `docs/design.md`, "Testing on
real systems".

To run the Linux scenarios without Docker (WSL, say), give them a home of their own:

```
HOME=$(mktemp -d) ACB_DISPOSABLE_MACHINE=1 ACB=path/to/acb FIXTURE=test/system/fixture/make-fake-steam.sh \
    CS2_STANDIN=path/to/a/copy/of/sleep/named/cs2 bash test/system/linux/scenarios.sh
```
