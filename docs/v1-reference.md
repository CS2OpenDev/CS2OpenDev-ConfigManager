# Advanced Config Backup

> v1, written while this lived in DemoViewer.NET as an extension, so paths and type names here use that
> layout (`src/Extensions/AdvancedConfigBackup/...`, `DemoViewer.NET.Extensions.ConfigBackup.*`); the code is
> now under `src/CS2OpenDev.ConfigManager.Core` and `plugin/CS2OpenDev.ConfigManager.DemoViewer`. The restorer
> described here (`Restore.ps1` and the kit it compiled) was replaced by `acb` and deleted; see
> [design.md](design.md). What is captured, and why, carries over.

A first-party extension (`net.demoviewer.pack.configbackup`) that saves a player's CS2 settings, and
the Windows and NVIDIA settings that matter to CS2, into one zip. The zip carries a script that applies
them on another PC and puts that PC back afterwards. It is for players who move between PCs: internet
cafes, LANs, tournament booths.

The extension adds one page, Settings, Extensions, ADVANCED CONFIG BACKUP, and runs nothing in the
background. The Desktop head ships it; the browser head does not, since it has no Steam, registry or
driver to read.

## What is saved

| Part (`-Only` name) | What | Where it comes from |
|---|---|---|
| `cs2` | Settings, binds, crosshair, video | `Steam/userdata/<account>/730/local/cfg`: `*.vcfg`, their `*.vcfg_lastclouded` twins, `cs2_video.txt` |
| `cs2game` | The player's own cfg files (`autoexec.cfg`, practice cfgs) | `game/csgo/cfg/**/*.cfg` minus Valve's: `gamemode_*` (but not the player's `gamemode_*_server.cfg`), `server.cfg` (unless it holds more than Valve's comment stub), `perftest.cfg`, `*_default.cfg`, `devonly/`. `gamestate_integration_*.cfg` only when asked |
| `launch` | CS2 launch options | `userdata/<account>/config/localconfig.vdf`, `UserLocalConfigStore/Software/Valve/Steam/apps/730/LaunchOptions` |
| `input` | Sticky, Toggle and Filter Keys hotkeys, when the player turned them off | `SystemParametersInfo`: the `*_HOTKEYACTIVE` bit only |
| `gamebar` | Game Mode, Game Bar recording | `HKCU\Software\Microsoft\GameBar`, `HKCU\System\GameConfigStore`, `HKCU\...\GameDVR` |
| `cs2exe` | High-performance GPU and "Disable fullscreen optimizations" for cs2.exe | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` (`GpuPreference=2` only), `HKCU\...\AppCompatFlags\Layers` (`DISABLEDXMAXIMIZEDWINDOWEDMODE` only), keyed by cs2.exe's path, which is rewritten to the target's |
| `power` | Active power plan | `powercfg`; by GUID and name, and the Windows template it is a copy of, never as an exported plan |
| `nvidia` | 14 NVIDIA Control Panel 3D settings, as cs2.exe gets them | NVAPI DRS; `NvidiaDrs.CarriedSettings` |
| `vibrance` | Digital vibrance | NVAPI DVC |

Left behind on purpose:

- `remote/` under the account folder and `remotecache.vdf`. That is Steam Cloud's folder, and Steam
  Cloud already brings it to any PC the player signs in on. Writing it would make a restore, or its undo,
  a change Steam uploads over the player's cloud copy, which then reaches their other PCs. The local
  `.vcfg` files go with their `_lastclouded` twins, so CS2 sees them as matching what it last sent to the
  cloud rather than as fresh local edits. How CS2 resolves the two against a newer cloud copy has not been
  tested. The restore applies capture's rules again (`ConfigBackup.IsRestorablePath`), so a backup from an
  earlier build or a hand-edited `backup.vdf` that names a `remote/` file or one of Valve's cfg files has
  that file left alone and named in the report. An undo snapshot is exempt: it puts back what its restore
  changed.
- Windows pointer speed, Enhance pointer precision, acceleration curves and keyboard repeat. CS2 reads raw
  input and has no setting to turn that off, so these change only the desktop, for everyone who uses the
  PC after the player.
- The primary display's resolution and refresh rate. CS2's own resolution and refresh rate are in
  `cs2_video.txt`; a desktop mode applied on another monitor could be a scaled or DSR mode that stays
  behind for the next person.
- A power plan of the player's own. Its `.pow` holds the source PC's processor, PCIe and USB power
  settings, which do not fit other hardware. The backup names it; a PC that lists a plan of that name
  switches to it. A plan named like a Windows template (in English, or as the capture PC lists that
  template) counts as a copy of it: `Ultimate Performance` enabled with `powercfg -duplicatescheme` has a
  random GUID, so the backup records the template beside it.
- Any other compatibility layer on cs2.exe (run as administrator, compatibility modes, DPI overrides) and
  any GPU preference other than high performance.
- Caches (`socache.dt`), ban lists, `trustedlaunch.cfg` (anti-cheat module list, machine-specific),
  screenshots, `.bak` files, Steam login data. The zip holds the Steam account number. Its file name holds
  the date and not the PC's name.
- The values of `rcon_password`, `sv_password`, `sv_setsteamaccount` (a server login token),
  `tv_password`, `tv_relaypassword` and `password`, in every `cs2game` file, in every `.vcfg` of the `cs2`
  part with its `_lastclouded` twin (CS2 saves every bind it was given in `cs2_user_keys*.vcfg`, and the
  archived `password` convar a player connected with in the convars files, where the whole
  `"password" "x"` line is removed) and in the launch options (`ConfigBackup.RemoveSecrets`,
  `ConfigBackup.MayHoldSecrets`). Each is removed with its value, whether it is a command on its own line,
  inside a bind or alias (`connect ip; password x`) or a `+command` in the launch options, so a practice
  server falls back to no rcon and no password; the capture report names the files and commands. The
  rest of each file is kept byte for byte, and both twins are filtered alike, so they still match. A
  secret written any other way (an `alias` holding only the value, a custom command name) is not
  recognised. The `auth` token in a `gamestate_integration_*.cfg` is carried as it is when the player
  asks for those files; the page's tooltip says so.

Not covered yet: AMD and Intel GPUs (Radeon Software saturation, Anti-Lag, Radeon Chill and Intel's
equivalents); the page says GPU settings are saved for NVIDIA only. NVIDIA display-level settings outside
the 3D profile: scaling mode (stretched 4:3 needs per-monitor display configuration through NVAPI),
G-SYNC enablement, output dynamic range and colour depth. NVIDIA's Ultra low latency mode, whose setting is
not in NVIDIA's public header. Hardware-accelerated GPU scheduling (HKLM, needs a reboot), Windows display
scaling, audio devices, and a Linux or macOS restore. Capture of the CS2 parts works on Linux and macOS;
the restore script is Windows only.

### The player's effective state, not only their changes

Where the target PC's own setting would otherwise survive and make CS2 behave unlike the player's PC, the
backup records what the player has, including "nothing set":

- `gamebar` records each value missing on the source as `absent`, and the restore deletes the target's.
- `nvidia` records, for each carried setting, the value cs2.exe gets on the source whether it is set on the
  CS2 profile or the global one. A setting left to the driver is recorded `absent`. The restore resets the
  target's CS2-profile value, and where cs2.exe would then inherit a value a user set on the target's
  global profile (a cafe's forced V-Sync or frame cap), it writes the driver's own value
  (`NVDRS_SETTING`'s predefined value on the global profile) onto the CS2 profile instead. The global
  profile itself is never written, and the undo snapshot records the CS2 profile as having no value, so
  `Undo.cmd` resets it and cs2.exe inherits the global value again. A driver that reports no predefined
  value leaves the setting as the global profile sets it, and the report names it.
- `cs2exe` merges: the player's fullscreen-optimizations choice is added to or removed from the target's
  Layers string, and every other layer the target has stays. A source without the layer records it
  `absent`, whether cs2.exe has no Layers value there or one holding other layers, so the restore takes the
  target's off. A high-performance GPU preference is set; without one, the target's preference stays.

## The archive

```
AdvancedConfigBackup-<yyyyMMdd-HHmm>.zip
  README.txt     what to do on the other PC
  Restore.cmd    runs Restore.ps1 with Windows PowerShell 5.1
  Undo.cmd       runs Restore.ps1 -Undo
  Restore.ps1    asks, checks CS2 and Steam are closed, calls the kit
  kit/*.cs       the kit's source, compiled on the other PC
  backup.vdf     the manifest, in Valve's KeyValues format
  files/cs2/...       relative to userdata/<account>/730
  files/cs2game/...   relative to game/csgo/cfg
```

`backup.vdf` holds `format` (1), `kind` (`backup` or `undo`), `created`, `creator`, then one section per part.
`cs2` and `launch` each name the account they came from; an undo snapshot also holds `user`, the SID of the
Windows user the restore ran as. Files are listed with their SHA-256, and every file of a part is checked, and
every merged file built, before the first is written; a damaged copy fails its part and writes nothing for it.
The same first pass opens each existing target with the rights the swap uses (write, write-attributes and
delete; a read-only target has its attribute cleared for the check and put back), so a file another user owns,
an ACL denies, or a program holds open also fails the part before any write. A failure the check cannot
foresee (the folder itself refusing the temporary copy, say) can still stop a part partway; every file is on
the undo record before it is written, so the undo puts it back. Files are written through a temporary copy
that is flushed to the disk (`FileOptions.WriteThrough` and `Flush(true)`) and then swapped in (`SafeFile`),
and the swapped-in file is flushed again, which on NTFS commits the rename. So a process killed mid-write, a
power loss or a bugcheck leaves the old file or the new one, never a torn or zero-filled `localconfig.vdf`.
The undo record is written the same way: the snapshot's `backup.vdf` (including the first one, which claims
the folder), every file copy in it and the `localconfig.vdf` copy are on the disk before the change they
record, so a registry hive or the NVIDIA settings store that persists a change cannot get ahead of its record.
For the undo record the flush after the swap is not best effort: if it fails, the change it guards is not made
(`SafeFile.WriteRecord`). A read-only target (a common way to stop CS2 resetting `cs2_video.txt`) is written
and stays read-only: `SafeFile` clears the attribute for the swap and puts it back. Undo snapshot copies never
carry the attribute, so an undo writes them back the same way.

Capture reads each file, takes passwords out in memory, and writes the staged copy once as a new file, so
an unfiltered copy never reaches the disk and a read-only source does not make a read-only copy. A part
that fails partway is removed from `backup.vdf` and its staged files deleted, and the zip is built from
what `backup.vdf` lists plus the scripts and kit (`BackupArchive.ArchiveEntries`), never from whatever is
in the staging folder. Deleting the staging folder afterwards is best effort and cannot turn a finished
backup into a reported failure. A capture in which no part was saved writes no zip, and the page says
nothing was saved; one that left parts out (failed or skipped) is announced as a warning naming them,
not as a success. A vibrance capture in which no display reports a level counts as skipped.

The Settings page looks for the NVIDIA driver by its file in System32 only; `nvapi64.dll` is first loaded
into DemoViewer when a backup is saved, never by opening Settings. A save checks for cancellation between
parts. The `nvidia` and `vibrance` capture parts run whole on a background thread of their own
(`ConfigBackup.RunOnDriverWorker`), writing into a manifest of their own that joins the backup only once
the part finishes. The save waits up to 60 seconds and watches for a cancel; past that, or on a cancel,
the part fails and the thread is left to finish alone, and while it is still running no later part or save
calls the driver again (each such part fails, saying to start the program again). `NvidiaDrs.Initialize` has its
own 30-second limit inside that, and `powercfg` is stopped after 30 seconds, so a hang fails its part
rather than holding the app's job queue. A cancelled save reads "The backup was cancelled." A native
crash inside `nvapi64.dll` still ends the process: the driver calls run in DemoViewer's own process, not
a child.

### A backup is a program

`Restore.cmd` runs `Restore.ps1`, which compiles and runs `kit/*.cs` from the same folder, with
`-ExecutionPolicy Bypass`. Whoever can change those files can run code as the player, and as
administrator if the player agrees to the NVIDIA part. The SHA-256 list in `backup.vdf` travels with the
files and is checked by code that also travels with them, so it catches a damaged copy, never a changed
one. The page, `README.txt` and the elevation question all say so: run only a backup you made and kept,
never one from a shared folder or from someone else.

What the extension does about it:

- **Check a backup** on the page compares an unzipped backup's `Restore.cmd`, `Undo.cmd`, `Restore.ps1`,
  `README.txt` and `kit/*.cs` byte for byte with the ones this version writes (`BackupArchive.Check`), and
  lists any other file in the folder Windows could run (`.dll`, `.exe`, `.ps1`, `.cmd` and the like). It
  needs DemoViewer, so it is for checking a backup before it goes to another PC or after it comes back;
  the restore itself runs where DemoViewer usually is not. A backup made by a released earlier version
  passes as that version's code when its scripts and kit, as one hash (`BackupArchive.SupportHash`), are
  in `BackupArchive.ReleasedSupportFiles`. That table is append-only and empty until the first release:
  each release adds its `CurrentSupportHash()`, and no entry is ever changed.
- The question that starts the elevated rerun defaults to no, says which folder's code would run as
  administrator, and is not asked at all under `-Yes`.
- The elevated rerun runs only the code the first run read. The first run reads `Restore.ps1` and each
  kit file once, compiles the kit from that text in memory (CodeDom, as `Add-Type` does), and hashes it.
  The elevated process is started by full path with `-EncodedCommand`, a short script holding those
  hashes: it reads `Restore.ps1`, refuses to go on if it differs, and runs it from memory with the hashes,
  and that run refuses a kit that differs. Both runs hash `Restore.ps1` from the same plain text read.
- The elevated rerun also acts on the folder as administrator (it writes the undo record, and an undo
  rewrites snapshots), so it runs only on a folder nobody else could have changed. `Get-FolderProblem`
  refuses, before the question is asked and again in the elevated process, a folder on a share or a
  network drive, on a drive without permissions (FAT32 and exFAT USB sticks), holding a junction, mount
  point or symbolic link (any reparse point whose tag has the name-surrogate bit, `WindowsSettings.IsLink`;
  OneDrive's Cloud Files placeholders redirect nothing and pass, so a synced Desktop works), or where an
  account other than the player, Administrators, SYSTEM or TrustedInstaller owns or may write the folder
  or anything in it, or may move aside a folder above it.
  That rules out a folder made directly under `C:\`, which Authenticated Users may modify; the player is
  told to copy the folder into their own user folder. The elevated undo works only on the snapshots the
  first run named in the bootstrap, and creates and moves no folder: the first run moves finished
  snapshots into `undo/applied/` after it returns. A file changed in the folder while the player answers
  the question therefore never runs as administrator, and nothing planted in it is acted on. Out of reach:
  a program already running as the player can change the compiler's temporary files, as with any
  `Add-Type`; Windows does not treat UAC as a boundary against the same user.
- `nvapi64.dll` and `wtsapi32.dll` load from System32 only (`DefaultDllImportSearchPaths`), and
  `powercfg.exe` and the elevated `powershell.exe` start by full path, so a file of that name in the
  backup folder or on `PATH` is never loaded. On a PC without an NVIDIA driver the NVIDIA parts report
  that there is none.

## One kit, two compilers

Everything that reads or writes a setting lives in `Kit/*.cs` in the extension project. The extension
compiles it on .NET 10 and captures with it; the archive carries the same files, which `Restore.ps1`
compiles in Windows PowerShell 5.1 with the .NET Framework's C# compiler through CodeDom (what `Add-Type`
uses), from text it has read and hashed (see above). That compiler is C# 5, so the kit is written in C#
5: no interpolation, `?.`, `nameof`, lambdas, `out var` or LINQ. Each file opens with `#nullable disable`
(removed when the archive is written) and `#pragma warning disable` (for the .NET 10 analyzers). The
files, and the scripts, are ASCII, so every reader decodes them alike and the elevated rerun's hashes
agree. `ArchiveTests` checks all of this, and its Windows test runs the real `powershell.exe` on a real
archive.

The point is that a restore on a PC without DemoViewer runs exactly the code the app's tests run.

## Restore

`Restore.cmd` on the other PC:

1. Shows what the backup holds.
2. Asks before touching anything other people on that PC share: the CS2 cfg folder (every Steam account
   and Windows user who plays CS2 there reads it), Windows and NVIDIA settings. `-GameOnly` restores only
   the player's own account: `cs2` and `launch`.
3. Waits for CS2 to close, since CS2 writes its settings back when it exits.
4. Offers to close Steam for the launch options, since Steam rewrites `localconfig.vdf` when it exits.
   With `-Yes` it never closes Steam unless `-CloseSteam` is also given.
5. Picks the Steam account for `cs2` and `launch`: `-Account`, else the backup's own account once it has
   signed in to Steam on that PC (Steam makes `userdata/<id>` then). An account id, from `-Account` or the
   manifest, must be all digits and a nonzero 32-bit number (`SteamInstall.NormalizeAccountId`; a
   SteamID64 is turned into one), and `-Account` must name an account with a `userdata` folder, so no id
   can name a path outside `userdata` or create a folder Steam never reads. It never falls back to the account
   that signed in last or the only one present: on a shared PC that is usually a stranger, whose config,
   and through Steam Cloud whose other PCs, would get the player's settings. When the backup's account has
   no folder, the script says to sign in and start CS2 once, and offers the accounts present as a menu
   whose default is to skip; with `-Yes` both parts are skipped. A write to an account other than the
   backup's says so in the report.
6. Records what it is about to replace under `undo/<sequence>-<UTC time>Z/`, in the same format, then
   applies. The sequence number is one past every snapshot under `undo/` and `undo/applied/`, the folder
   is claimed by creating its `backup.vdf` exclusively, and `backup.vdf` records the number as
   `sequence`; the time is only for people to read. A claim then looks again for another folder with its
   number and, if it finds one, gives the number up and takes the next; each claim creates its folder
   before it looks, so of two overlapping restores at least the later one moves on. So two restores never
   share a folder or a number, and a clock that goes back (the end of daylight saving time, a PC that syncs a stale clock)
   cannot reorder the undo. A restore that changes nothing claims no folder. The
   snapshot's `backup.vdf` is saved before each change, not once at the end, so a restore that dies partway
   (a crash in a driver call, a closed console, a pulled USB stick) leaves a record of everything it
   touched. Steam's `localconfig.vdf` is copied into the snapshot before the launch options are written;
   nothing is left beside Steam's file.
7. Offers to rerun the parts that need an administrator (the NVIDIA settings) elevated; see "A backup is
   a program". A cancelled or refused Windows prompt is reported, the parts already applied stay, and
   the run ends with exit code 2.

The undo record lives only in the backup folder, so the shared parts (`cs2game` and the machine-wide
ones) apply by default only from a folder that stays on the PC. When the folder is on a network share or
drive, a drive without file permissions (FAT32, exFAT), a drive Windows does not list as fixed, or a disk
on a USB bus (many USB drives report as fixed; read with `Get-Partition | Get-Disk`, best effort), or the
player passes `-Removable`, the script says to copy the folder onto the PC and run it from there, and
applies only the player's own-account parts unless the player types `y` (default no) or passes
`-SharedFromHere` under `-Yes`. A USB drive that reports as fixed on a PC without the Storage module is
not spotted. If the folder goes before `Undo.cmd` runs (a cafe profile cleared at sign-out, or a drive
removed after an explicit yes), the shared cfg files, Windows and NVIDIA settings stay as the restore left
them; a copy of the record kept on the PC itself is not built, since a folder outside the user's profile
that a later elevated undo reads needs its own answer to who may write it.

Exit codes: 0 done; 1 nothing was changed (declined, nothing selected, or a PC that allows PowerShell only
in constrained language mode, which the script checks first); 2 some parts failed, or a restore's
administrator parts were not run, whichever way that came about (not offered for the folder, declined,
the Windows prompt refused, or skipped under `-Yes`); 3 and 4 the elevated rerun refused (a changed code
file, a folder others could change or whose permissions cannot be read); 5 an undo that left parts on the
PC (deferred, waiting, needing an administrator, or a finished snapshot that could not be filed into
`undo/applied/`). An undo snapshot whose record cannot be read is reported failed and is never reported
as put back. `Restore.cmd` and `Undo.cmd` pass the code on. They say Windows did not let PowerShell run,
so nothing changed, only for 9009 and 1260 (not found, or blocked by AppLocker or a software restriction
policy); any other code outside 0 to 5 (a crash, Ctrl+C, a closed console) is reported as the script
stopping partway, with the advice to run `Undo.cmd`. An execution policy set by Group Policy shows
PowerShell's own message, which README.txt's "IF IT WILL NOT RUN" explains.

`input`, `gamebar` and `cs2exe` write the profile of the Windows user the process runs as. When that is not
the user signed in to the session (an administrator's password typed at a UAC prompt, or Restore.cmd run
with Run as administrator), those parts are deferred rather than written to the administrator's profile.
An undo applies them only as the user its snapshot recorded (`user`), so an administrator's old values
never land in the player's profile (`ConfigBackup.OtherUserReason`).

`cs2_video.txt` and `cs2_machine_convars.vcfg` are merged rather than copied. The target keeps its own
`VendorID`, `DeviceID`, `setting.monitor_index`, `setting.knowndevice` and `Autoconfig` in the video file,
and its own `voice_device_override`, `speaker_config`, `joy_name` and `trusted_launch` in the machine
convars; a hardware key the target lacks, or every one when the target has no usable copy of the file, is
dropped rather than carried, so the source's microphone or monitor is never named on a PC without it and
CS2 detects the target's own. Quality levels and the refresh rate are the player's choices
in CS2's video menu and travel.

NVIDIA settings are written to the CS2 profile only, never the global one, so no other game or app on the
PC changes. Only ids in `NvidiaDrs.CarriedSettings` are written; any other id in a backup is skipped and
reported, as is a setting the target's driver refuses. Every carried setting is a DWORD, so a backup
entry giving one as another type, or a value that is not a number, fails the part before the driver is
opened (`ConfigBackup.NvidiaEntryProblem`). An undo snapshot puts back every entry that passes the same
check and reports the rest, so one bad record does not keep the others on the PC; and a restore never
replaces a setting the target's driver holds in a form that check would refuse, so the snapshots it
writes always pass it. `SetSetting` also refuses a binary value longer
than the 4096 bytes `NVDRS_BINARY_SETTING` holds. A driver with no CS2 profile skips the part; the
restore never creates a profile.

Vibrance is applied only when the target has exactly one NVIDIA display and the backup holds one level
(or the same level for every display): NVAPI's display order says nothing about which monitor is which.
An undo whose display is not listed now (a monitor that is off or asleep drops out of NVAPI's list) defers
that display rather than skipping it, so the record stays for the next `Undo.cmd`.

The power plan is reached by GUID, else by a listed plan of the same name, else by the template the backup
records (a listed template GUID under a localized name). A Windows template the target does not list
(Ultimate Performance, often High performance) is copied with `powercfg /duplicatescheme`; the copy's GUID
goes into the undo snapshot and `Undo.cmd` deletes it after switching back, only when it is a bare GUID,
not a Windows template, not the active plan, and still listed (`ConfigBackup.CreatedPlanProblem`;
`DeletePowerPlan` refuses anything but a non-template GUID itself). A `powercfg /duplicatescheme` stopped
before it returns (the console closed, the process killed, or the kit's own 30-second `powercfg` timeout)
can leave an inactive copy of the Windows template that `Undo.cmd` does not know about and does not
remove; the plan in use is still switched back, since the previous plan was recorded before the copy.

`-Only` takes the parts by their names in the table above; a name that is not one is reported and dropped,
and a selection left empty (for example `-GameOnly -Only nvidia`) applies nothing rather than everything.
The elevated rerun passes the same `-Only`, so it applies only the parts that asked for an administrator.

`input` only turns a hotkey off, keeping the rest of the target's accessibility flags and Filter Keys'
timings; it never turns a feature or a hotkey on.

A backup can name only the registry values in `WindowsSettings.RegistryItems`, each as the kind Windows
reads it (`dword` for the Game Bar values, `string` for the two cs2exe values), and only paths that stay
inside their folder (`SafeRelative`); anything else fails its part before any of it is written
(`ConfigBackup.RegistryEntryProblem`). Capture treats a value of another kind on the source (a tweak
script's string `"0"`) as unset, since Windows does not read it. An undo snapshot writes back whatever kind
the target had, `REG_EXPAND_SZ` included.

Each part is classified once, in `Sections.Scope` (own account, shared files, per user, machine), and
every rule (which parts need the per-user check, which count as shared, which `-GameOnly` keeps, which
exist only on Windows) is read from it; `Restore.ps1` asks the kit rather than keeping its own list. A part
the restore has no handler for fails, and a part that reports nothing is not counted as put back, so its
undo record is kept.

`Undo.cmd` applies every snapshot under `undo/`, newest first by sequence number. A file that already
matches its record is left alone. A part that cannot be put back now is
reported `deferred` (CS2 or Steam running, Steam or CS2 not found, the NVIDIA driver not loading, another
Windows user), unlike `skipped`, which means nothing is left to do. As each part is put back, or found to
need nothing, it is removed from the snapshot's `backup.vdf`, so running `Undo.cmd` again applies only what
is left, and `-Only` undoes only the parts named. A part a newer snapshot still holds after its turn (it
failed, was deferred or was not selected, or the snapshot could not be read) is held back from every older
snapshot in that run and reported `waiting`: the older record is what the PC had first, so it has to be
applied after the newer one, never before it. A snapshot moves into `undo/applied/` once no part is
left, through .NET calls rather than cmdlets so `[` and `]` in the folder's path are not read as wildcards,
retrying for a moment while a virus scanner or OneDrive holds a copy. One that still will not move is
already put back: it is reported, the older snapshots are still undone, and the next `Undo.cmd` files it
away. A snapshot that cannot be read is reported and left in place while the others are still undone; one
with no `backup.vdf` changed nothing, since the record is written before each change. Undoing writes no
snapshot of its own. What it leaves on the PC: registry keys that
`CreateSubKey` made (each part's keys exist on any Windows install, so in practice none). The README tells
the player to delete the unzipped folder if it was unzipped onto the PC itself, since `undo/` holds copies
of the files the restore replaced there.

## Verified, and not

v1's suite covered the kit under Windows PowerShell 5.1 (C# 5), KeyValues parsing, capture and restore into a
fake Steam folder, the merges, launch options and account choice, the path, registry and per-user rules, undo
snapshots part by part, and `Restore.ps1` itself, including its elevated rerun's encoded command run without
elevation. The plugin loaded in the Desktop app.

Not verified, because doing so changes the machine's own settings: restoring `input`, `gamebar`,
`cs2exe`, `power`, `nvidia` and `vibrance`, and undoing them. The elevated rerun has not been run under
UAC; its encoded command is tested without elevation. The per-user
check has not been seen to defer under over-the-shoulder elevation. Their write paths are the documented Windows
and NVAPI calls, and the decisions in front of them are unit tested. The NVIDIA capture's reading of an
inherited global value through the CS2 profile (`NvAPI_DRS_GetSetting`'s `settingLocation`) follows NVIDIA's
header and has not been run since the allowlist replaced the user-set enumeration. Neither has the
restore's reading of a global override (`settingLocation`) and of the driver's predefined value
(`isPredefinedValid` and the predefined union at offsets 4116 and 4120). Whether
`powercfg /duplicatescheme` needs an administrator on a given PC is not known; a refusal fails the part.
A power plan restore whose active plan cannot be read fails rather than switching with nothing to switch
back to. A restore killed between `/duplicatescheme` and recording the copy's GUID leaves one unrecorded,
inactive copy of a Windows template listed; nothing tries to find it by name, since that could delete a
plan the PC's owner made.
The vibrance calls (`0x4085DE45`, `0x172409B4`) are not in NVIDIA's public header; reading works, setting
is untested. The Settings page itself has not been looked at on screen; its compiled bindings are checked
at build.

## Packaging

The manifest is stamped like the Strat Book's, from `src/Extensions/AdvancedConfigBackup/version.json`
(0.1.x). Its copy beside the DLL is named `net.demoviewer.pack.configbackup.extension.json`
(`ExtensionManifestCopyName` in the SDK targets), so it does not overwrite the Strat Book's
`extension.json` in the head output; `ShippedPack.BesideApp` reads the id-named copy first.
`PackagingTests` checks the build produces that copy, no bare `extension.json`, and a copy equal to the
embedded one. It has no release workflow or update feed yet: `pack-extension.sh` and
`release-extension.yml` still know only the Strat Book.
