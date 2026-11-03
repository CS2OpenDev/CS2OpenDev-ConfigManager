#!/usr/bin/env bash
# Runs a published acb binary end to end: backup, verify, plan, restore and undo between two fake Steam folders
# in a temp folder, with the player's undo store redirected into it too. It never touches the machine's own Steam:
# every command that writes names a fake folder with --steam-path, and only the file sections (cs2, cs2game,
# launch) are captured, so nothing reaches the registry, the power plan or the NVIDIA driver. Run as an
# administrator (GitHub's Windows runners), it checks that acb leaves those parts instead.
#
# Usage: acb-smoke.sh <path to acb or acb.exe>
set -euo pipefail

acb="$1"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# acb.exe on Windows takes Windows paths; Git Bash's own are POSIX.
native() {
    if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

account=69413322
steamid=$((76561197960265728 + account))

fake_steam() {
    local root="$1" autoexec="$2" launch="$3"
    local cfg="$root/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg"
    local user="$root/userdata/$account"
    local escaped
    escaped="$(native "$root" | sed 's/\\/\\\\/g')"
    mkdir -p "$cfg" "$user/730/local/cfg" "$user/config" "$root/config"
    printf '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"apps"\n\t\t{\n\t\t\t"730"\t\t"1"\n\t\t}\n\t}\n}\n' "$escaped" \
        > "$root/steamapps/libraryfolders.vdf"
    printf '"AppState"\n{\n\t"appid"\t\t"730"\n\t"installdir"\t\t"Counter-Strike Global Offensive"\n}\n' > "$root/steamapps/appmanifest_730.acf"
    printf '%s\n' "$autoexec" > "$cfg/autoexec.cfg"
    printf '"config"\n{\n\t"convars"\n\t{\n\t\t"volume"\t\t"0.3"\n\t}\n}\n' > "$user/730/local/cfg/cs2_machine_convars.vcfg"
    printf '"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n\t\t\t\t{\n\t\t\t\t\t"730"\n\t\t\t\t\t{\n\t\t\t\t\t\t"LaunchOptions"\t\t"%s"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n' "$launch" \
        > "$user/config/localconfig.vdf"
    printf '"users"\n{\n\t"%s"\n\t{\n\t\t"AccountName"\t\t"someone"\n\t\t"MostRecent"\t\t"1"\n\t}\n}\n' "$steamid" > "$root/config/loginusers.vdf"
}

expect() {
    local want="$1"; shift
    local got=0
    "$acb" "$@" > "$work/out.txt" 2>&1 || got=$?
    if [ "$got" != "$want" ]; then
        cat "$work/out.txt"
        echo "acb $1 exited $got; expected $want" >&2
        exit 1
    fi
    echo "ok: acb $1 (exit $got)"
}

same() {
    if [ "$(cat "$1")" != "$2" ]; then
        echo "$1 holds '$(cat "$1")'; expected '$2'" >&2
        exit 1
    fi
}

fake_steam "$work/source" "echo source" "-novid"
fake_steam "$work/target" "echo target" "-target"
export LOCALAPPDATA
LOCALAPPDATA="$(native "$work/localappdata")"
export XDG_STATE_HOME="$work/state"
source_root="$(native "$work/source")"
target_root="$(native "$work/target")"
zip="$(native "$work/backup.zip")"
target_autoexec="$work/target/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/autoexec.cfg"

says() {
    if ! grep -qF "$1" "$work/out.txt"; then
        cat "$work/out.txt"
        echo "acb did not say: $1" >&2
        exit 1
    fi
}

# GitHub's Windows runners run jobs as an administrator with UAC off, and an administrator's acb writes nothing
# through the Steam folder. There the restore leaves every part this backup holds, and that is what is checked.
elevated=0
if [ "$(uname -s)" != "Linux" ] && net session > /dev/null 2>&1; then
    elevated=1
fi

expect 0 --version
expect 0 backup -o "$zip" --only cs2,cs2game,launch --steam-path "$source_root"
expect 0 verify "$zip"

# A self-contained backup carries this binary. Unpacked the way Extract All and unzip -d do it, the carried copy
# runs on its own (nothing else in its folder) and says it is the same program as the one that made the backup.
sc="$work/AdvancedConfigBackup-smoke"
expect 0 backup -o "$(native "$sc.zip")" --only cs2game --steam-path "$source_root" --self-contained
expect 0 verify "$(native "$sc.zip")"
says "Restorer: restorer/"
if [ "$(uname -s)" = "Linux" ]; then
    unzip -q "$sc.zip" -d "$sc"
    if [ ! -x "$sc/restorer/acb" ]; then
        echo "restorer/acb did not unpack runnable" >&2
        exit 1
    fi
    carried="$sc/restorer/acb"
else
    powershell.exe -NoProfile -Command "Expand-Archive -LiteralPath '$(native "$sc.zip")' -DestinationPath '$(native "$sc")'"
    carried="$sc/restorer/acb.exe"
fi
"$carried" verify "$(native "$sc.zip")" > "$work/out.txt" 2>&1 || { cat "$work/out.txt"; echo "the carried restorer failed" >&2; exit 1; }
says "the same program as this acb"
echo "ok: the carried restorer runs alone and is this acb"

if [ "$elevated" = 1 ]; then
    expect 2 plan "$zip" --json --steam-path "$target_root"
    says "writes nothing through the Steam folder"
    expect 2 restore "$zip" --yes --steam-path "$target_root"
    says "writes nothing through the Steam folder"
    same "$target_autoexec" "echo target"
    echo "ok: as an administrator, nothing was written through the Steam folder"
    exit 0
fi

expect 0 plan "$zip" --json --steam-path "$target_root"
same "$target_autoexec" "echo target"
expect 0 restore "$zip" --yes --steam-path "$target_root"
same "$target_autoexec" "echo source"
expect 0 undo --list
expect 0 undo --yes --steam-path "$target_root"
same "$target_autoexec" "echo target"

if [ "$(uname -s)" = "Linux" ]; then
    mode="$(stat -c %a "$XDG_STATE_HOME/cs2opendev/configbackup/undo")"
    if [ "$mode" != "700" ]; then
        echo "the undo store has mode $mode; expected 700" >&2
        exit 1
    fi
    echo "ok: undo store is mode 700"
fi
