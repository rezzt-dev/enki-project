#!/usr/bin/env bash
# Incorpora el motor oficial de VideoLAN sin instalar VLC.app en el equipo del usuario.
# Solo se ejecuta al empaquetar, en macOS. Versión y hashes fijados para builds reproducibles.
set -euo pipefail

RID="${1:?Falta el RID}"
DESTINATION="${2:?Falta el directorio de salida}"
VLC_VERSION=3.0.24
case "$RID" in
  osx-arm64) VLC_ARCH=arm64; MACH_ARCH=arm64; VLC_SHA256=64a89d93cdd30b0e97131743e246373db82d6826ea882d265d46d74b136da2b7 ;;
  osx-x64) VLC_ARCH=intel64; MACH_ARCH=x86_64; VLC_SHA256=1ef6c903e2dc026d4e58ddf7b2a9ed0eb6f8caf80a9a9f46f96490340b962707 ;;
  *) echo "RID multimedia no admitido: $RID" >&2; exit 1 ;;
esac
command -v hdiutil >/dev/null || { echo "El motor de macOS debe empaquetarse en macOS." >&2; exit 1; }
WORK_DIR="$(mktemp -d)"
MOUNT="$WORK_DIR/vlc"
MOUNTED=0
cleanup() {
  if [[ "$MOUNTED" -eq 1 ]]; then hdiutil detach "$MOUNT" -quiet || return; fi
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT
DMG="$WORK_DIR/vlc.dmg"
BASE_URL="https://download.videolan.org/pub/videolan/vlc/$VLC_VERSION"
curl --fail --location --retry 3 --connect-timeout 30 --max-time 600 \
  "$BASE_URL/macosx/vlc-$VLC_VERSION-$VLC_ARCH.dmg" -o "$DMG"
printf '%s  %s\n' "$VLC_SHA256" "$DMG" | shasum -a 256 -c -
hdiutil attach "$DMG" -readonly -nobrowse -mountpoint "$MOUNT" -quiet
MOUNTED=1
SOURCE="$MOUNT/VLC.app/Contents/MacOS"
[[ -s "$SOURCE/lib/libvlc.dylib" && -d "$SOURCE/plugins" ]] || {
  echo "La distribución oficial no contiene el motor esperado." >&2; exit 1;
}
lipo -verify_arch "$MACH_ARCH" "$SOURCE/lib/libvlc.dylib"
mkdir -p "$DESTINATION"
cp -a "$SOURCE/lib" "$SOURCE/plugins" "$DESTINATION/"
# VLC.app aporta @rpath desde su ejecutable. Enki necesita rutas relativas a cada biblioteca.
# Se firma el resultado después, dentro del bundle definitivo de Enki.
while IFS= read -r -d '' library; do
  install_name_tool -add_rpath '@loader_path' "$library"
  codesign --force --sign - "$library"
done < <(find "$DESTINATION/lib" -type f -name '*.dylib' -print0)
while IFS= read -r -d '' plugin; do
  install_name_tool -add_rpath '@loader_path/../lib' "$plugin"
  codesign --force --sign - "$plugin"
done < <(find "$DESTINATION/plugins" -type f -name '*.dylib' -print0)
for license in GPL-2.0 LGPL-2.1; do
  cp "$(dirname "$0")/licenses/$license.txt" "$DESTINATION/"
done
# Conserva las licencias que acompañan la distribución nativa y su procedencia.
for license in "$MOUNT/VLC.app/Contents/Resources/COPYING"* "$MOUNT/Read Me.rtf"; do
  if [[ -f "$license" ]]; then cp "$license" "$DESTINATION/"; fi
done
cat > "$DESTINATION/SOURCE.txt" <<NOTICE
Motor multimedia VideoLAN VLC $VLC_VERSION ($VLC_ARCH). Rutas de carga ajustadas para el bundle de Enki.
Distribución: $BASE_URL/macosx/vlc-$VLC_VERSION-$VLC_ARCH.dmg
SHA-256: $VLC_SHA256
Código fuente: $BASE_URL/vlc-$VLC_VERSION.tar.xz
NOTICE

# Prueba el motor incluido; no consulta /Applications/VLC.app ni Homebrew.
if [[ "$(uname -m)" == "$MACH_ARCH" ]]; then
  MEDIA_CHECK=(python3)
else
  # La arquitectura ajena se valida estáticamente con lipo; la prueba dinámica corresponde a su runner.
  echo "==> Motor $MACH_ARCH incluido; prueba dinámica omitida en $(uname -m)"
  exit 0
fi
"${MEDIA_CHECK[@]}" - "$DESTINATION" <<'PYTHON'
import ctypes
import os
import pathlib
import sys

root = pathlib.Path(sys.argv[1]).resolve()
os.environ["VLC_PLUGIN_PATH"] = str(root / "plugins")
ctypes.CDLL(str(root / "lib/libvlccore.dylib"), mode=ctypes.RTLD_GLOBAL)
vlc = ctypes.CDLL(str(root / "lib/libvlc.dylib"))
vlc.libvlc_new.argtypes = [ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
vlc.libvlc_new.restype = ctypes.c_void_p
vlc.libvlc_release.argtypes = [ctypes.c_void_p]
instance = vlc.libvlc_new(0, None)
if not instance:
    raise SystemExit("No se pudo iniciar el motor multimedia empaquetado.")
vlc.libvlc_release(instance)
print("==> Motor multimedia incluido y verificado")
PYTHON
