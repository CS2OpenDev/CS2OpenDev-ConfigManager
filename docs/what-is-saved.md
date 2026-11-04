# What a backup saves

What `acb` and the DemoViewer.NET plugin put in a backup, what they leave out on purpose, and how each section is
written back. How a backup is packaged, trusted and undone is in [design.md](design.md). The rules live in
`src/CS2OpenDev.ConfigManager.Core/Kit`; names in backticks below are the code that holds them.

## Sections

| Section (`--only` name) | What | Where it comes from |
|---|---|---|
| `cs2` | Settings, binds, crosshair, video | `Steam/userdata/<account>/730/local/cfg`: `*.vcfg`, their `*.vcfg_lastclouded` twins, `cs2_video.txt`; files directly in that folder only (`ConfigBackup.IsAccountSetting`) |
| `cs2game` | The player's own cfg files (`autoexec.cfg`, practice cfgs) | `game/csgo/cfg/**/*.cfg` minus Valve's: `gamemode_*` (but not the player's `gamemode_*_server.cfg`), `server.cfg` (unless it holds more than Valve's comment stub), `perftest.cfg`, `*_default.cfg`, `devonly/`. `gamestate_integration_*.cfg` only with `--gsi` |
| `launch` | CS2 launch options | `userdata/<account>/config/localconfig.vdf`, `UserLocalConfigStore/Software/Valve/Steam/apps/730/LaunchOptions` |
| `input` | Sticky, Toggle and Filter Keys hotkeys, when the player turned them off | `SystemParametersInfo`: the `*_HOTKEYACTIVE` bit only |
| `gamebar` | Game Mode, Game Bar recording | `HKCU\Software\Microsoft\GameBar`, `HKCU\System\GameConfigStore`, `HKCU\...\GameDVR` |
| `cs2exe` | High-performance GPU and "Disable fullscreen optimizations" for cs2.exe | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` (`GpuPreference=2` only), `HKCU\...\AppCompatFlags\Layers` (`DISABLEDXMAXIMIZEDWINDOWEDMODE` only), keyed by cs2.exe's path, which is rewritten to the target's |
| `power` | Active power plan | `powercfg`; by GUID and name, and the Windows template it is a copy of, never as an exported plan |
| `nvidia` | On Windows, 14 NVIDIA Control Panel 3D settings as cs2.exe gets them. On Linux, the driver's application profile settings for `cs2` | Windows: NVAPI DRS, `NvidiaDrs.CarriedSettings`. Linux: `~/.nv/nvidia-application-profiles-rc` and `.rc.d`, `NvidiaProfiles.CarriedKeys` |
| `vibrance` | Digital vibrance | NVAPI DVC |

On Linux, `input`, `gamebar`, `cs2exe`, `power` and `vibrance` are skipped as Windows only. A backup restores only
on the platform it was made on.

Capture skips any file over 4 MiB and any reparse point or link.

## Left out on purpose

- `remote/` under the account folder and `remotecache.vdf`. That is Steam Cloud's folder, and Steam Cloud already
  brings it to any PC the player signs in on. Writing it would make a restore, or its undo, a change Steam uploads
  over the player's cloud copy, which then reaches their other PCs. The local `.vcfg` files go with their
  `_lastclouded` twins, so CS2 sees them as matching what it last sent to the cloud rather than as fresh local
  edits. How CS2 resolves the two against a newer cloud copy has not been tested.
- Windows pointer speed, Enhance pointer precision, acceleration curves and keyboard repeat. CS2 reads raw input
  and has no setting to turn that off, so these change only the desktop, for everyone who uses the PC after the
  player.
- The primary display's resolution and refresh rate. CS2's own resolution and refresh rate are in
  `cs2_video.txt`; a desktop mode applied on another monitor could be a scaled or DSR mode that stays behind for
  the next person.
- A power plan of the player's own. Its `.pow` holds the source PC's processor, PCIe and USB power settings, which
  do not fit other hardware. The backup names it; a PC that lists a plan of that name switches to it. A plan named
  like a Windows template (in English, or as the capture PC lists that template) counts as a copy of it: `Ultimate
  Performance` enabled with `powercfg -duplicatescheme` has a random GUID, so the backup records the template
  beside it.
- Any other compatibility layer on cs2.exe (run as administrator, compatibility modes, DPI overrides) and any GPU
  preference other than high performance.
- On Linux, application profile keys that hold a path (`GLShaderDiskCachePath`), name this PC's hardware
  (`GLSyncDisplayDevice`, `EGLVisible*`) or loosen protocol or security checks. A rule whose pattern is compound
  or matches a library rather than plainly naming the `cs2` process is left, and the report says so.
- Caches (`socache.dt`), ban lists, `trustedlaunch.cfg` (anti-cheat module list, machine-specific), screenshots,
  `.bak` files, Steam login data. The zip holds the Steam account number. Its file name
  (`AdvancedConfigBackup-<yyyyMMdd-HHmmss>.zip`) holds the date and not the PC's name.

### Passwords and server tokens

The values of `rcon_password`, `sv_password`, `sv_setsteamaccount` (a server login token), `tv_password`,
`tv_relaypassword` and `password` (`ConfigBackup.SecretCommands`) are removed from every `cs2game` file, from every
`.vcfg` of the `cs2` section with its `_lastclouded` twin, and from the launch options (`ConfigBackup.RemoveSecrets`,
`ConfigBackup.MayHoldSecrets`). CS2 saves every bind it was given in `cs2_user_keys*.vcfg`, and the archived
`password` convar a player connected with in the convars files, where the whole `"password" "x"` line is removed.

Each is removed with its value, whether it is a command on its own line, inside a bind or alias
(`connect ip; password x`) or a `+command` in the launch options, so a practice server falls back to no rcon and no
password; the capture report names the files and commands. The rest of each file is kept byte for byte, and both
twins are filtered alike, so they still match. A secret written any other way (an `alias` holding only the value, a
custom command name) is not recognised. The `auth` token in a `gamestate_integration_*.cfg` is carried as it is when
the player asks for those files; the plugin's tooltip says so.

### Not covered yet

AMD and Intel GPUs (Radeon Software saturation, Anti-Lag, Radeon Chill and Intel's equivalents); the plugin says GPU
settings are saved for NVIDIA only. NVIDIA display-level settings outside the 3D profile: scaling mode (stretched
4:3 needs per-monitor display configuration through NVAPI), G-SYNC enablement, output dynamic range and colour
depth. NVIDIA's Ultra low latency mode, whose setting is not in NVIDIA's public header. Hardware-accelerated GPU
scheduling (HKLM, needs a reboot), Windows display scaling, audio devices. Vibrance and power on Linux. macOS.

## The player's effective state, not only their changes

Where the target PC's own setting would otherwise survive and make CS2 behave unlike the player's PC, the backup
records what the player has, including "nothing set":

- `gamebar` records each value missing on the source as `absent`, and the restore deletes the target's.
- `nvidia` (Windows) records, for each carried setting, the value cs2.exe gets on the source whether it is set on
  the CS2 profile or the global one. A setting left to the driver is recorded `absent`. The restore resets the
  target's CS2-profile value, and where cs2.exe would then inherit a value a user set on the target's global
  profile (a cafe's forced V-Sync or frame cap), it writes the driver's own value (`NVDRS_SETTING`'s predefined
  value on the global profile) onto the CS2 profile instead. The global profile itself is never written, and the
  undo record holds the CS2 profile as having no value, so `acb undo` resets it and cs2.exe inherits the global
  value again. A driver that reports no predefined value leaves the setting as the global profile sets it, and the
  report names it.
- `cs2exe` merges: the player's fullscreen-optimizations choice is added to or removed from the target's Layers
  string, and every other layer the target has stays. A source without the layer records it `absent`, whether
  cs2.exe has no Layers value there or one holding other layers, so the restore takes the target's off. A
  high-performance GPU preference is set; without one, the target's preference stays.

## How each section is written back

The restore applies capture's rules again (`ConfigBackup.IsRestorablePath`), so a backup from an earlier build or a
hand-edited `backup.vdf` that names a `remote/` file or one of Valve's cfg files has that file left alone and named
in the report. It also never writes a name that could be an 8.3 alias, refuses a file entry without a SHA-256 or
listed twice, writes game state integration files only with `--gsi`, and leaves the files an organiser policy says
to leave (`LeaveFiles`). An undo record is exempt from the capture rules: it puts back what its restore changed.

`cs2_video.txt` and `cs2_machine_convars.vcfg` are merged rather than copied. The target keeps its own `VendorID`,
`DeviceID`, `setting.monitor_index`, `setting.knowndevice` and `Autoconfig` in the video file
(`ConfigBackup.VideoHardwareKeys`), and its own `voice_device_override`, `speaker_config`, `joy_name` and
`trusted_launch` in the machine convars (`ConfigBackup.MachineHardwareKeys`). A hardware key the target lacks, or
every one when the target has no usable copy of the file, is dropped rather than carried, so the source's
microphone or monitor is never named on a PC without it and CS2 detects the target's own. Quality levels and the
refresh rate are the player's choices in CS2's video menu and travel. An undo does not merge; it writes back the
bytes it recorded.

A launch value holding a control character is captured with a warning and refused at restore.

`input` only turns a hotkey off, keeping the rest of the target's accessibility flags and Filter Keys' timings; a
restore never turns a feature or a hotkey on. An undo can turn a hotkey back on, since that is what the PC had.

A backup can name only the registry values in `WindowsSettings.RegistryItems`, each by its exact key and name and as
the kind Windows reads it (`dword` for the Game Bar values, `string` for the two cs2exe values); anything else fails
its section before any of it is written (`ConfigBackup.RegistryEntryProblem`). Capture treats a value of another
kind on the source (a tweak script's string `"0"`) as unset, since Windows does not read it. An undo record writes
back whatever kind the target had, `REG_EXPAND_SZ` included.

NVIDIA settings on Windows are written to the CS2 profile only, never the global one, so no other game or app on the
PC changes. Only ids in `NvidiaDrs.CarriedSettings` are written, less any an organiser policy denies
(`NvidiaDeny`); any other id in a backup is skipped and reported, as is a setting the target's driver refuses. Every
carried setting is a DWORD, so a backup entry giving one as another type, a value that is not a number, or an id
listed twice fails the section before the driver is opened (`ConfigBackup.NvidiaEntryProblem`). An undo record puts
back every entry that passes the same check and reports the rest, so one bad entry does not keep the others on the
PC; a setting the driver refuses on undo stays in the record and the section is deferred. A restore never replaces
a setting the target's driver holds in a form that check would refuse, so the records it writes always pass it.
`SetSetting` also refuses a binary value longer than the 4096 bytes `NVDRS_BINARY_SETTING` holds. A driver with no
CS2 profile skips the section; the restore never creates a profile. NVIDIA is the only section that needs an
administrator (`RestoreRules.NeedsAdministrator`).

NVIDIA on Linux never edits the player's files: the restore writes one file of its own,
`~/.nv/nvidia-application-profiles-rc.d/50-acb-cs2`, and `acb undo` removes it or puts back the copy it replaced.
[design.md](design.md), "Platforms and sections", has the details.

Vibrance is applied only when the target has exactly one NVIDIA display and the backup holds one level (or the same
level for every display): NVAPI's display order says nothing about which monitor is which. An undo whose display is
not listed now (a monitor that is off or asleep drops out of NVAPI's list), or whose driver does not start, is
deferred rather than skipped, so the record stays for the next `acb undo`.

The power plan is reached by GUID, else by a listed plan of the same name, else by the template the backup records
(a listed template GUID under a localized name). A Windows template the target does not list (Ultimate
Performance, often High performance) is copied with `powercfg /duplicatescheme`. The undo record notes the copy
before `powercfg` runs and then records its GUID; `acb undo` deletes it after switching back, only when it is a bare
GUID, not a Windows template, not the active plan, and still listed (`ConfigBackup.CreatedPlanProblem`;
`WindowsSettings.DeletePowerPlan` refuses anything but a non-template GUID itself). If `powercfg` fails or times
out (30 seconds) and exactly one new non-template plan is then listed, that plan is recorded as the copy and
removed on undo; otherwise the undo says a copy may be left. A power plan restore whose active plan cannot be read
fails rather than switching with nothing to switch back to. Whether `/duplicatescheme` needs an administrator on a
given PC is not known; a refusal fails the section.

Each section is classified once, in `Sections.Scope` (own account, shared files, per user, machine). A section the
restore has no handler for fails, and a section that reports nothing is not counted as put back, so its undo record
is kept. `--only` takes the section names in the table above; a name that is not one is a usage error.

## Not yet checked on real hardware

The NVIDIA capture's reading of an inherited global value through the CS2 profile (`NvAPI_DRS_GetSetting`'s
`settingLocation`), the restore's reading of a global override and of the driver's predefined value
(`isPredefinedValid` and the predefined union at offsets 4116 and 4120), and setting vibrance (`0x4085DE45`,
`0x172409B4`, which are not in NVIDIA's public header) follow NVIDIA's header but have not been run on a real
driver. [design.md](design.md), "Testing on real systems", has the spare-PC checklist that covers them.
