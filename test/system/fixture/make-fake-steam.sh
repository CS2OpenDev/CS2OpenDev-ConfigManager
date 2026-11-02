#!/usr/bin/env bash
# Lays out a Steam folder shaped like an installed one, with CS2 installed and one signed-in account, so acb can be
# run against it without Steam or CS2. Nothing here comes from Valve: every file is written from scratch, holding
# only the keys acb reads (libraryfolders.vdf, appmanifest_730.acf, userdata/<account>, localconfig.vdf,
# loginusers.vdf) and a zero-byte stand-in for the cs2 binary.
#
# Usage: make-fake-steam.sh <steam root> [account id] [autoexec text] [launch options]
#
# The root may be anything; to have acb find it the way it finds a real install on Linux, pass
# ~/.local/share/Steam (and see --link), or Flatpak's ~/.var/app/com.valvesoftware.Steam/.local/share/Steam.
#   --link   also make ~/.steam/steam a link to the root, as the Steam client does.
set -euo pipefail

link=0
if [ "${1:-}" = "--link" ]; then link=1; shift; fi

root="$1"
account="${2:-69413322}"
autoexec="${3:-echo fixture}"
launch="${4:--novid}"
steamid=$((76561197960265728 + account))

# Windows paths when run under Git Bash, so the libraryfolders.vdf path is one acb.exe can follow.
native() {
    if command -v cygpath > /dev/null; then cygpath -w "$1"; else printf '%s' "$1"; fi
}

case "$(uname -s)" in
    Linux) bin="game/bin/linuxsteamrt64/cs2" ;;
    *) bin="game/bin/win64/cs2.exe" ;;
esac

install="$root/steamapps/common/Counter-Strike Global Offensive"
cfg="$install/game/csgo/cfg"
user="$root/userdata/$account"
mkdir -p "$cfg" "$(dirname "$install/$bin")" "$user/730/local/cfg" "$user/config" "$root/config"
escaped="$(native "$root" | sed 's/\\/\\\\/g')"

printf '"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"%s"\n\t\t"apps"\n\t\t{\n\t\t\t"730"\t\t"1"\n\t\t}\n\t}\n}\n' "$escaped" \
    > "$root/steamapps/libraryfolders.vdf"
printf '"AppState"\n{\n\t"appid"\t\t"730"\n\t"installdir"\t\t"Counter-Strike Global Offensive"\n}\n' \
    > "$root/steamapps/appmanifest_730.acf"
: > "$install/$bin"

# Valve's own cfg files, which acb must leave alone, beside the player's.
printf 'mp_maxrounds 24\n' > "$cfg/gamemode_competitive.cfg"
: > "$cfg/server.cfg"
printf '%s\n' "$autoexec" > "$cfg/autoexec.cfg"
mkdir -p "$cfg/practice" && printf 'sv_cheats 1\n' > "$cfg/practice/nades.cfg"

printf '"config"\n{\n\t"convars"\n\t{\n\t\t"volume"\t\t"0.3"\n\t}\n}\n' > "$user/730/local/cfg/cs2_machine_convars.vcfg"
printf '"config"\n{\n\t"convars"\n\t{\n\t\t"sensitivity"\t\t"1.1"\n\t}\n}\n' > "$user/730/local/cfg/cs2_user_convars_0_slot0.vcfg"
cp "$user/730/local/cfg/cs2_user_convars_0_slot0.vcfg" "$user/730/local/cfg/cs2_user_convars_0_slot0.vcfg_lastclouded"
printf '"config"\n{\n\t"bindings"\n\t{\n\t\t"MOUSE4"\t\t"slot7"\n\t}\n}\n' > "$user/730/local/cfg/cs2_user_keys_0_slot0.vcfg"
printf '"video.cfg"\n{\n\t"setting.defaultres"\t\t"1920"\n\t"setting.defaultresheight"\t\t"1080"\n}\n' > "$user/730/local/cfg/cs2_video.txt"
printf '"UserLocalConfigStore"\n{\n\t"Software"\n\t{\n\t\t"Valve"\n\t\t{\n\t\t\t"Steam"\n\t\t\t{\n\t\t\t\t"apps"\n\t\t\t\t{\n\t\t\t\t\t"730"\n\t\t\t\t\t{\n\t\t\t\t\t\t"LaunchOptions"\t\t"%s"\n\t\t\t\t\t}\n\t\t\t\t}\n\t\t\t}\n\t\t}\n\t}\n}\n' "$launch" \
    > "$user/config/localconfig.vdf"
printf '"users"\n{\n\t"%s"\n\t{\n\t\t"AccountName"\t\t"someone"\n\t\t"MostRecent"\t\t"1"\n\t}\n}\n' "$steamid" > "$root/config/loginusers.vdf"

if [ "$link" = 1 ]; then
    mkdir -p "$HOME/.steam"
    ln -sfn "$root" "$HOME/.steam/steam"
fi
echo "fake Steam at $root (account $account)"
