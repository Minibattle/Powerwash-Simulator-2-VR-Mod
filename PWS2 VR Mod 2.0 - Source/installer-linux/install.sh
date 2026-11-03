#!/usr/bin/env bash
# PowerWash VR - Linux installer (Steam + Proton)
#
# Puts into the PowerWash Simulator 2 folder:
#   1. MelonLoader v0.7.3 (the mod loader)          - from its GitHub release
#   2. Unity's OpenXR plugin 1.18.0 (three files)   - from Unity's package registry
#   3. Mods/PowerWashVR.dll (this mod)              - from this folder
# and the .NET 6 runtime (x64, Windows) into the game's Proton prefix, which
# MelonLoader needs. Downloads of 1 and 2 are checked against known SHA-256
# hashes. Nothing is installed outside the game folder and its Proton prefix.
#
#   bash install.sh [path/to/PowerWash Simulator 2]
#
# Needs: curl, unzip, tar, sha256sum. The game must have been started once
# through Steam (that creates its Proton prefix).

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EXE="PowerWash Simulator 2.exe"

ML_URL="https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip"
ML_SHA="5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416"
OXR_PKG="https://download.packages.unity.com/com.unity.xr.openxr/-/com.unity.xr.openxr-1.18.0.tgz"
OXR_MIRROR="https://github.com/needle-mirror/com.unity.xr.openxr/raw/1.18.0/"
OXR_FILES=(
  "RuntimeLoaders/windows/x64/openxr_loader.dll|PowerWash Simulator 2_Data/Plugins/x86_64/openxr_loader.dll|f7384429c16288d9c2c123acfca1e040c9cec6c5484a3f5a5107d6aa32ba57e8"
  "Runtime/windows/x64/UnityOpenXR.dll|PowerWash Simulator 2_Data/Plugins/x86_64/UnityOpenXR.dll|a344bf5428d70d41e91bd36aa85677206a66224949f9c830d8ded4f1d612be66"
  "Runtime/UnitySubsystemsManifest.json|PowerWash Simulator 2_Data/UnitySubsystems/UnityOpenXR/UnitySubsystemsManifest.json|2528d0caa5fb4c4ea91ed74fb88315fca934158314bcec29d12fbeaac8e7cfd6"
)
DOTNET_URL="https://builds.dotnet.microsoft.com/dotnet/Runtime/6.0.36/dotnet-runtime-6.0.36-win-x64.zip"

say()  { printf '%s\n' "$*"; }
ok()   { printf '\033[32m%s\033[0m\n' "$*"; }
warn() { printf '\033[33m%s\033[0m\n' "$*"; }
fail() { printf '\n\033[31m%s\033[0m\n\n' "$*"; exit 1; }
hash() { sha256sum "$1" | cut -d' ' -f1; }

for tool in curl unzip tar sha256sum; do
  command -v "$tool" >/dev/null 2>&1 || fail "'$tool' is needed (install it with your package manager)."
done

# ---- find the game -------------------------------------------------------
steam_roots() {
  for root in "$HOME/.steam/steam" "$HOME/.local/share/Steam" "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam" "$HOME/snap/steam/common/.local/share/Steam"; do
    [ -d "$root/steamapps" ] && printf '%s\n' "$root"
  done
}

libraries() {
  steam_roots | while read -r root; do
    printf '%s\n' "$root"
    vdf="$root/steamapps/libraryfolders.vdf"
    [ -f "$vdf" ] && grep -Po '"path"\s+"\K[^"]+' "$vdf" || true
  done | awk '!seen[$0]++'
}

GAME="${1:-}"
LIB=""
if [ -z "$GAME" ]; then
  if [ -f "$HERE/$EXE" ]; then GAME="$HERE"; fi
  if [ -z "$GAME" ] && [ -f "$HERE/../$EXE" ]; then GAME="$(cd "$HERE/.." && pwd)"; fi
  if [ -z "$GAME" ]; then
    while read -r lib; do
      candidate="$lib/steamapps/common/PowerWash Simulator 2"
      if [ -f "$candidate/$EXE" ]; then GAME="$candidate"; LIB="$lib"; break; fi
    done < <(libraries)
  fi
fi
[ -n "$GAME" ] && [ -f "$GAME/$EXE" ] || fail "PowerWash Simulator 2 was not found. Run: bash install.sh \"/path/to/PowerWash Simulator 2\""
GAME="$(cd "$GAME" && pwd)"
[ -n "$LIB" ] || LIB="$(cd "$GAME/../../.." && pwd)"
say "Game: $GAME"

[ -f "$HERE/Mods/PowerWashVR.dll" ] || fail "Mods/PowerWashVR.dll is missing next to this script."

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ---- 1. MelonLoader ----------------------------------------------------------
if [ -f "$GAME/version.dll" ] && [ -f "$GAME/MelonLoader/net6/MelonLoader.dll" ]; then
  say "MelonLoader: already installed"
else
  say "MelonLoader 0.7.3:"
  curl -fL --progress-bar -o "$TMP/ml.zip" "$ML_URL"
  [ "$(hash "$TMP/ml.zip")" = "$ML_SHA" ] || fail "The MelonLoader download does not match its known checksum. Nothing was changed."
  unzip -oq "$TMP/ml.zip" -d "$GAME"
  ok "  installed"
fi

# ---- 2. OpenXR ---------------------------------------------------------------
need=()
for entry in "${OXR_FILES[@]}"; do
  IFS='|' read -r src dst sha <<<"$entry"
  if [ ! -f "$GAME/$dst" ] || [ "$(hash "$GAME/$dst")" != "$sha" ]; then need+=("$entry"); fi
done
if [ "${#need[@]}" -eq 0 ]; then
  say "Unity OpenXR 1.18.0: already installed"
else
  say "Unity OpenXR 1.18.0:"
  mkdir -p "$TMP/oxr"
  from_pkg=0
  if curl -fsL -o "$TMP/oxr.tgz" "$OXR_PKG" && tar -xzf "$TMP/oxr.tgz" -C "$TMP/oxr"; then from_pkg=1; else warn "  Unity's registry was not reachable, trying the GitHub mirror"; fi
  for entry in "${need[@]}"; do
    IFS='|' read -r src dst sha <<<"$entry"
    file="$TMP/oxr/package/$src"
    if [ "$from_pkg" -ne 1 ] || [ ! -f "$file" ]; then
      file="$TMP/oxr/$(basename "$src")"
      curl -fsL -o "$file" "$OXR_MIRROR$src" || fail "$src could not be downloaded (Unity's registry and the GitHub mirror both failed)."
    fi
    [ "$(hash "$file")" = "$sha" ] || fail "$src does not match its known checksum. Nothing more was changed."
    mkdir -p "$(dirname "$GAME/$dst")"
    cp -f "$file" "$GAME/$dst"
  done
  ok "  installed"
fi

# ---- 3. The mod --------------------------------------------------------------
mkdir -p "$GAME/Mods"
cp -f "$HERE/Mods/PowerWashVR.dll" "$GAME/Mods/PowerWashVR.dll"
ok "PowerWash VR: installed"

# ---- 4. .NET 6 in the Proton prefix -----------------------------------------
APPID=""
for manifest in "$LIB"/steamapps/appmanifest_*.acf; do
  [ -f "$manifest" ] || continue
  if grep -q '"installdir"[[:space:]]*"PowerWash Simulator 2"' "$manifest"; then
    APPID="$(basename "$manifest" .acf)"; APPID="${APPID#appmanifest_}"; break
  fi
done
PFX=""
[ -n "$APPID" ] && PFX="$LIB/steamapps/compatdata/$APPID/pfx"
if [ -n "$PFX" ] && [ -d "$PFX/drive_c" ]; then
  DOTNET="$PFX/drive_c/Program Files/dotnet"
  if ls "$DOTNET/shared/Microsoft.NETCore.App" 2>/dev/null | grep -q '^6\.'; then
    say ".NET 6 runtime: already in the Proton prefix"
  else
    say ".NET 6 runtime (into the Proton prefix):"
    if curl -fL --progress-bar -o "$TMP/dotnet.zip" "$DOTNET_URL" && unzip -tq "$TMP/dotnet.zip" >/dev/null 2>&1; then
      mkdir -p "$DOTNET"
      unzip -oq "$TMP/dotnet.zip" -d "$DOTNET"
      ok "  installed"
    elif command -v protontricks >/dev/null 2>&1; then
      warn "  direct download failed; using protontricks (this can take a few minutes)"
      protontricks "$APPID" -q dotnet6 && ok "  installed" || warn "  protontricks did not finish: run  protontricks $APPID dotnet6  yourself"
    else
      warn "  could not be downloaded. Install it yourself, then start the game:"
      warn "    protontricks $APPID dotnet6"
      warn "  (or unzip $DOTNET_URL into the prefix's C:\\Program Files\\dotnet)"
    fi
  fi
else
  warn "The game's Proton prefix was not found (start the game once through Steam, then run this again),"
  warn "or install .NET 6 into it yourself:  protontricks ${APPID:-<appid>} dotnet6"
fi

say ""
ok "Done. One more step in Steam:"
say "  Right-click PowerWash Simulator 2 > Properties > General > Launch Options, and enter:"
say ""
say '      WINEDLLOVERRIDES="version=n,b" %command%'
say ""
say "  (Proton loads MelonLoader's version.dll only with that.) Then start SteamVR and the game."
say "  The first launch takes a minute while MelonLoader prepares itself."
