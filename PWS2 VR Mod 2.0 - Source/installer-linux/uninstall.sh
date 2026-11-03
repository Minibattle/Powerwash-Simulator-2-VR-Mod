#!/usr/bin/env bash
# PowerWash VR - Linux uninstaller: removes the mod, MelonLoader and the
# OpenXR files from the game folder (the .NET runtime in the Proton prefix is
# left; it is harmless).
#
#   bash uninstall.sh [path/to/PowerWash Simulator 2]

set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="PowerWash Simulator 2.exe"
GAME="${1:-}"
if [ -z "$GAME" ]; then
  for root in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"; do
    vdf="$root/steamapps/libraryfolders.vdf"
    for lib in "$root" $( [ -f "$vdf" ] && grep -Po '"path"\s+"\K[^"]+' "$vdf" ); do
      if [ -f "$lib/steamapps/common/PowerWash Simulator 2/$EXE" ]; then GAME="$lib/steamapps/common/PowerWash Simulator 2"; break 2; fi
    done
  done
fi
[ -n "$GAME" ] && [ -f "$GAME/$EXE" ] || { echo "PowerWash Simulator 2 was not found. Run: bash uninstall.sh \"/path/to/PowerWash Simulator 2\""; exit 1; }

rm -f  "$GAME/Mods/PowerWashVR.dll"
rmdir "$GAME/Mods" 2>/dev/null || true
rm -f  "$GAME/version.dll"
rm -rf "$GAME/MelonLoader"
rm -f  "$GAME/PowerWash Simulator 2_Data/Plugins/x86_64/openxr_loader.dll" \
       "$GAME/PowerWash Simulator 2_Data/Plugins/x86_64/UnityOpenXR.dll"
rm -rf "$GAME/PowerWash Simulator 2_Data/UnitySubsystems/UnityOpenXR"
echo "Removed. Also clear the game's Launch Options in Steam (the WINEDLLOVERRIDES line)."
echo "Your mod settings stay in $GAME/UserData (delete it too for a clean slate)."
