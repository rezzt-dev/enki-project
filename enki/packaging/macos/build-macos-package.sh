#!/usr/bin/env bash
#
# Empaqueta Enki para macOS: publica el build self-contained (dotnet publish), lo envuelve en un
# bundle Enki.app válido (Contents/MacOS + Contents/Resources + Info.plist) siguiendo la
# documentación oficial de Avalonia sobre despliegue en macOS, y genera:
#
#   - Enki-<version>-macos-<arch>.zip   — siempre (Enki.app comprimido).
#   - Enki-<version>-macos-<arch>.dmg   — solo si `hdiutil` está disponible (macOS real; el job de
#     macOS en CI lo trae siempre). En Linux/Windows se omite con un aviso.
#
# El bundle se firma ad-hoc (`codesign --sign -`) cuando `codesign` está disponible: no sustituye a
# una firma con certificado de Apple Developer (seguiría avisando "desarrollador no identificado" al
# abrirlo), pero evita el "Enki.app está dañado" que Gatekeeper muestra para bundles sin firmar en
# macOS recientes.
#
# Uso:
#   enki/packaging/macos/build-macos-package.sh [opciones]
#
# Opciones:
#   --rid <RID>           osx-arm64 (por defecto, Apple Silicon) u osx-x64 (Intel).
#   --version <X.Y.Z>      Por defecto, el valor de <Version> en enki/Directory.Build.props.
#   --output-dir <dir>     Por defecto, dist/macos en la raíz del repo.
#   --skip-publish         Reusa enki/publish/<RID> ya existente en vez de volver a compilar.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

RID="osx-arm64"
VERSION=""
OUTPUT_DIR="$REPO_ROOT/dist/macos"
SKIP_PUBLISH=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --version) VERSION="$2"; shift 2 ;;
    --output-dir) OUTPUT_DIR="$2"; shift 2 ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Opción desconocida: $1" >&2; exit 1 ;;
  esac
done

case "$RID" in
  osx-arm64) MAC_ARCH="arm64" ;;
  osx-x64) MAC_ARCH="x86_64" ;;
  *) echo "RID no soportado: $RID (usa osx-arm64 o osx-x64)" >&2; exit 1 ;;
esac

if [[ -z "$VERSION" ]]; then
  # sed portable (no grep -P): el grep de serie de macOS es BSD grep y no entiende PCRE lookbehind.
  VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$REPO_ROOT/enki/Directory.Build.props" | head -1)"
fi
if [[ -z "$VERSION" ]]; then
  echo "No se pudo determinar la versión — pásala explícitamente con --version X.Y.Z" >&2
  exit 1
fi

echo "==> Enki $VERSION ($RID / $MAC_ARCH)"

PUBLISH_DIR="$REPO_ROOT/publish/$RID"

if [[ "$SKIP_PUBLISH" -eq 0 ]]; then
  echo "==> dotnet publish ($RID, self-contained, single-file)"
  dotnet publish "$REPO_ROOT/enki/src/enki/enki.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=false \
    -p:Version="$VERSION" \
    --output "$PUBLISH_DIR"
else
  echo "==> --skip-publish: reusando $PUBLISH_DIR"
fi

if [[ ! -x "$PUBLISH_DIR/enki" ]]; then
  echo "No se encuentra un ejecutable 'enki' en $PUBLISH_DIR — ¿falló el publish?" >&2
  exit 1
fi

# También impide empaquetar publicaciones antiguas incompletas con --skip-publish.
if [[ ! -s "$PUBLISH_DIR/polar.properties" ]] || ! grep -q '^SUPABASE_URL=https://' "$PUBLISH_DIR/polar.properties" || ! grep -q '^SUPABASE_ANON_KEY=.' "$PUBLISH_DIR/polar.properties"; then
  echo "Falta la configuración pública de Polar en $PUBLISH_DIR. Vuelve a ejecutar sin --skip-publish con Supabase configurado." >&2
  exit 1
fi

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

APP="$WORK_DIR/Enki.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# Igual que en el .deb/.tar.gz de Linux: el binario y sus librerías nativas (libSkiaSharp.dylib,
# libHarfBuzzSharp.dylib...) tienen que quedarse juntos porque .NET las resuelve vía
# AppContext.BaseDirectory, que dentro de un bundle apunta a Contents/MacOS. local.properties (las
# propiedades locales de quien compila) se excluye. polar.properties sí viaja con la URL y
# la clave pública que necesita la aplicación instalada.
cp -a "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
rm -f "$APP/Contents/MacOS/local.properties"
chmod 755 "$APP/Contents/MacOS/enki"

cp "$REPO_ROOT/enki/src/enki/assets/icon/enki.icns" "$APP/Contents/Resources/enki.icns"

COPYRIGHT="$(sed -n 's/.*<Copyright>\([^<]*\)<\/Copyright>.*/\1/p' "$REPO_ROOT/enki/Directory.Build.props" | head -1)"
sed -e "s/__ENKI_VERSION__/$VERSION/g" -e "s/__ENKI_COPYRIGHT__/$COPYRIGHT/g" \
  "$SCRIPT_DIR/Info.plist.template" > "$APP/Contents/Info.plist"

if command -v codesign >/dev/null 2>&1; then
  echo "==> firma ad-hoc (codesign --sign -)"
  codesign --force --deep --sign - "$APP"
else
  echo "==> codesign no disponible (normal fuera de macOS) — Enki.app queda sin firmar"
fi

mkdir -p "$OUTPUT_DIR"

# --- .zip (siempre) -----------------------------------------------------------------------------
ZIP_FILE="$OUTPUT_DIR/Enki-${VERSION}-macos-${MAC_ARCH}.zip"
rm -f "$ZIP_FILE"
if command -v ditto >/dev/null 2>&1; then
  # ditto conserva los atributos extendidos del bundle (incl. la firma) mejor que zip; es la
  # herramienta que recomienda Apple para distribuir un .app comprimido.
  ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP_FILE"
else
  (cd "$WORK_DIR" && zip -r -q "$ZIP_FILE" "Enki.app")
fi
echo "==> $ZIP_FILE"

# --- .dmg (solo en macOS real, donde hdiutil existe) ---------------------------------------------
if command -v hdiutil >/dev/null 2>&1; then
  DMG_FILE="$OUTPUT_DIR/Enki-${VERSION}-macos-${MAC_ARCH}.dmg"
  rm -f "$DMG_FILE"
  DMG_STAGE="$WORK_DIR/dmg"
  mkdir -p "$DMG_STAGE"
  cp -a "$APP" "$DMG_STAGE/Enki.app"
  ln -s /Applications "$DMG_STAGE/Applications"
  hdiutil create -volname "Enki $VERSION" -srcfolder "$DMG_STAGE" -ov -format UDZO "$DMG_FILE" >/dev/null
  echo "==> $DMG_FILE"
else
  echo "==> hdiutil no disponible (normal fuera de macOS) — se omite el .dmg, usa el .zip"
fi

echo "==> listo"
