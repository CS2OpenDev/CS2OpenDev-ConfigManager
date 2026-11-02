#!/usr/bin/env bash
# acb on a Linux "player PC": a non-root user whose home holds a fake Steam where a real one lives
# (~/.local/share/Steam, with ~/.steam/steam linked to it), found by acb itself with no --steam-path, and the real
# default undo store under ~/.local/state. It deletes and rewrites Steam folders in $HOME, so it runs only where
# ACB_DISPOSABLE_MACHINE=1 says the home is thrown away afterwards: the test image sets it, a developer's shell never
# should. To run it outside the image, point HOME at an empty temp folder first.
#
# ACB (default /opt/acb/acb) and FIXTURE (default /opt/acb-fixture/make-fake-steam.sh) name the binary and fixture;
# CS2_STANDIN names a program called cs2 to run as "CS2 running" (default: cs2 on PATH, which the image provides).
set -euo pipefail

if [ "${ACB_DISPOSABLE_MACHINE:-}" != 1 ]; then
    echo "Refusing to run: these scenarios delete and rewrite Steam folders in \$HOME. Set ACB_DISPOSABLE_MACHINE=1 only in a throwaway container, or with HOME pointed at an empty temp folder." >&2
    exit 1
fi

if [ "$(id -u)" = 0 ]; then
    echo "Run as a non-root user: root ignores file modes, so the read-only rows would pass without testing anything." >&2
    exit 1
fi

acb="${ACB:-/opt/acb/acb}"
fixture="${FIXTURE:-/opt/acb-fixture/make-fake-steam.sh}"
cs2="${CS2_STANDIN:-cs2}"
work="$(mktemp -d)"
pass=0

ok() { echo "ok: $*"; pass=$((pass + 1)); }
fail() { cat "$work/out.txt" 2>/dev/null || true; echo "FAIL: $*" >&2; exit 1; }
expect() {
    local want="$1"; shift
    local got=0
    "$acb" "$@" > "$work/out.txt" 2>&1 || got=$?
    [ "$got" = "$want" ] || fail "acb $* exited $got; expected $want"
}
holds() { [ "$(cat "$1")" = "$2" ] || fail "$1 holds '$(cat "$1")'; expected '$2'"; }

steam="$HOME/.local/share/Steam"
store="$HOME/.local/state/cs2opendev/configbackup/undo"
autoexec="$steam/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/autoexec.cfg"

# The source PC, somewhere acb is told about; the target PC, where acb finds it.
bash "$fixture" "$work/source" 69413322 "echo source" "-novid" > /dev/null
bash "$fixture" --link "$steam" 69413322 "echo target" "-target" > /dev/null
expect 0 backup -o "$work/backup.zip" --only cs2,cs2game,launch --steam-path "$work/source"
expect 0 verify "$work/backup.zip"
ok "backup and verify"

# 1. Discovery through ~/.steam/steam, restore and undo with the default store.
expect 0 plan "$work/backup.zip"
holds "$autoexec" "echo target"
expect 0 restore "$work/backup.zip" --yes
holds "$autoexec" "echo source"
[ "$(stat -c %a "$store")" = 700 ] || fail "the undo store is not mode 700"
expect 0 undo --yes
holds "$autoexec" "echo target"
ok "found Steam through ~/.steam/steam, restored and undid; store mode 700"

# 2. CS2 running: a process named cs2 is all acb looks for.
"$cs2" 300 & cs2pid=$!
expect 2 restore "$work/backup.zip" --yes
holds "$autoexec" "echo target"
kill "$cs2pid"; wait "$cs2pid" 2>/dev/null || true
expect 0 undo --yes
ok "cs2 running: its sections left, exit 2"

# 3. A cfg folder the player may not add files to: the section is left, exit 2.
cfgdir="$(dirname "$autoexec")"
chmod a-w "$cfgdir"
expect 2 restore "$work/backup.zip" --yes --only cs2game
holds "$autoexec" "echo target"
chmod u+w "$cfgdir"
expect 0 undo --yes
ok "read-only cfg folder: section left, exit 2"

# 4. The Linux NVIDIA part: the player's application profile for cs2 travels as acb's own file in the .d folder,
#    and the target's own rc file is left byte for byte.
mkdir -p "$work/source-home/.nv" "$HOME/.nv"
printf '# player\n{ "rules": [ { "pattern": "cs2", "profile": [ "GLThreadedOptimizations", true, "GLSyncToVblank", 0 ] } ] }\n' \
    > "$work/source-home/.nv/nvidia-application-profiles-rc"
printf '{ "rules": [ { "pattern": "glxgears", "profile": [ "GLSyncToVblank", 1 ] } ] }\n' > "$HOME/.nv/nvidia-application-profiles-rc"
cp "$HOME/.nv/nvidia-application-profiles-rc" "$work/target-rc"
HOME="$work/source-home" "$acb" backup -o "$work/nvidia.zip" --only nvidia --steam-path "$work/source" > "$work/out.txt" 2>&1 \
    || fail "backup of the NVIDIA profile"
expect 0 restore "$work/nvidia.zip" --yes
grep -q GLThreadedOptimizations "$HOME/.nv/nvidia-application-profiles-rc.d/50-acb-cs2" || fail "acb's profile file lacks the setting"
cmp -s "$HOME/.nv/nvidia-application-profiles-rc" "$work/target-rc" || fail "the target's own rc file changed"
expect 0 undo --yes
[ ! -e "$HOME/.nv/nvidia-application-profiles-rc.d/50-acb-cs2" ] || fail "undo left acb's profile file"
ok "NVIDIA application profile: acb's own file written, the target's left, undo removed it"

# 5. Flatpak's root, with the native ones gone.
rm -rf "$steam" "$HOME/.steam"
flat="$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
bash "$fixture" "$flat" 69413322 "echo flatpak" "-target" > /dev/null
expect 0 restore "$work/backup.zip" --yes
holds "$flat/steamapps/common/Counter-Strike Global Offensive/game/csgo/cfg/autoexec.cfg" "echo source"
expect 0 undo --yes
ok "Flatpak Steam root found and restored"

echo "all $pass scenarios passed"
