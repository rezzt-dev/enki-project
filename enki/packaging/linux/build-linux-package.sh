#!/usr/bin/env bash
#
# Empaqueta Enki para Linux: publica el build self-contained (dotnet publish), arma un árbol de
# instalación FHS (usr/bin, usr/lib, usr/share/applications, usr/share/icons/hicolor) y genera:
#
#   - enki_<version>_<arch>.deb        — solo si `dpkg-deb` está instalado (Debian/Ubuntu y derivados).
#   - enki-<version>-linux-<arch>.tar.gz — siempre. Incluye install.sh/uninstall.sh para distros sin
#     `dpkg-deb` (Arch, Fedora, openSUSE...) — el mismo árbol usr/ que usa el .deb, copiado a /usr con
#     sudo. Es la vía recomendada para probar el paquete en esta máquina de desarrollo (Arch Linux).
#
# Uso:
#   enki/packaging/linux/build-linux-package.sh [opciones]
#
# Opciones:
#   --rid <RID>          linux-x64 (por defecto) o linux-arm64.
#   --version <X.Y.Z>     Por defecto, el valor de <Version> en enki/Directory.Build.props.
#   --output-dir <dir>    Por defecto, dist/linux en la raíz del repo.
#   --skip-publish        Reusa enki/publish/<RID> ya existente en vez de volver a compilar.
#
# Requiere el SDK de .NET (ver enki/global.json) salvo con --skip-publish. `dpkg-deb` es opcional;
# sin él, el script sigue generando el .tar.gz y avisa de que el .deb se ha omitido.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../../.." && pwd)"

RID="linux-x64"
VERSION=""
OUTPUT_DIR="$REPO_ROOT/dist/linux"
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
  linux-x64) DEB_ARCH="amd64" ;;
  linux-arm64) DEB_ARCH="arm64" ;;
  *) echo "RID no soportado: $RID (usa linux-x64 o linux-arm64)" >&2; exit 1 ;;
esac

if [[ -z "$VERSION" ]]; then
  VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$REPO_ROOT/enki/Directory.Build.props" | head -1)"
fi
if [[ -z "$VERSION" ]]; then
  echo "No se pudo determinar la versión — pásala explícitamente con --version X.Y.Z" >&2
  exit 1
fi

echo "==> Enki $VERSION ($RID / $DEB_ARCH)"

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

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

STAGE="$WORK_DIR/stage"
mkdir -p "$STAGE/usr/lib/enki" "$STAGE/usr/bin" "$STAGE/usr/share/applications"
mkdir -p "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/icons/hicolor/512x512/apps"
mkdir -p "$STAGE/usr/share/doc/enki"

# Copia el publish tal cual, salvo local.properties: son las credenciales de Supabase del entorno de
# desarrollo de quien compila (ver enki.csproj y docs/supabase-sync/08-configuracion-y-credenciales.md);
# nunca deben viajar dentro de un paquete distribuido, con o sin build de Debug.
cp -a "$PUBLISH_DIR/." "$STAGE/usr/lib/enki/"
rm -f "$STAGE/usr/lib/enki/local.properties"
chmod 755 "$STAGE/usr/lib/enki/enki"

cat > "$STAGE/usr/bin/enki" <<'WRAPPER'
#!/bin/sh
# Wrapper de instalación FHS — el binario real vive junto a las librerías nativas que necesita
# (libSkiaSharp.so, libHarfBuzzSharp.so...), que Enki resuelve vía AppContext.BaseDirectory.
exec /usr/lib/enki/enki "$@"
WRAPPER
chmod 755 "$STAGE/usr/bin/enki"

cp "$SCRIPT_DIR/enki.desktop" "$STAGE/usr/share/applications/enki.desktop"
cp "$REPO_ROOT/enki/src/enki/assets/icon/enki-256.png" "$STAGE/usr/share/icons/hicolor/256x256/apps/enki.png"
cp "$REPO_ROOT/enki/src/enki/assets/icon/enki-512.png" "$STAGE/usr/share/icons/hicolor/512x512/apps/enki.png"
cp "$REPO_ROOT/LICENSE" "$STAGE/usr/share/doc/enki/copyright"

mkdir -p "$OUTPUT_DIR"

# --- .deb (Debian/Ubuntu y derivados) ---------------------------------------------------------
if command -v dpkg-deb >/dev/null 2>&1; then
  DEB_STAGE="$WORK_DIR/deb"
  cp -a "$STAGE" "$DEB_STAGE"
  mkdir -p "$DEB_STAGE/DEBIAN"
  INSTALLED_SIZE_KB="$(du -sk "$STAGE" | cut -f1)"

  cat > "$DEB_STAGE/DEBIAN/control" <<CONTROL
Package: enki
Version: $VERSION
Section: office
Priority: optional
Architecture: $DEB_ARCH
Maintainer: rezztdev <rezztdev@users.noreply.github.com>
Installed-Size: $INSTALLED_SIZE_KB
Description: Tu segundo cerebro, en tu escritorio
 Enki es una aplicacion de escritorio de notas Markdown, Kanban, calendario y
 recordatorios pensada para un unico usuario. Funciona por completo sin
 conexion; la sincronizacion en la nube (Polar) es opcional.
CONTROL

  DEB_FILE="$OUTPUT_DIR/enki_${VERSION}_${DEB_ARCH}.deb"
  dpkg-deb --root-owner-group --build "$DEB_STAGE" "$DEB_FILE" >/dev/null
  echo "==> $DEB_FILE"
else
  echo "==> dpkg-deb no está instalado — se omite el .deb (el .tar.gz sigue generándose)"
fi

# --- .tar.gz portable (cualquier distro con glibc, incl. Arch) --------------------------------
TARBALL_ROOT="$WORK_DIR/tarball"
mkdir -p "$TARBALL_ROOT"
cp -a "$STAGE/usr" "$TARBALL_ROOT/usr"

cat > "$TARBALL_ROOT/install.sh" <<'INSTALL'
#!/usr/bin/env bash
# Instala Enki copiando el árbol usr/ de este paquete dentro de /usr del sistema (requiere sudo).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sudo cp -a "$here/usr/." /usr/
command -v update-desktop-database >/dev/null 2>&1 && sudo update-desktop-database /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && sudo gtk-update-icon-cache -qf /usr/share/icons/hicolor || true
echo "Enki instalado. Búscalo en el menú de aplicaciones o ejecuta 'enki' en una terminal."
INSTALL

cat > "$TARBALL_ROOT/uninstall.sh" <<'UNINSTALL'
#!/usr/bin/env bash
# Elimina exactamente los ficheros que instala install.sh.
set -euo pipefail
sudo rm -rf /usr/lib/enki /usr/share/doc/enki
sudo rm -f /usr/bin/enki /usr/share/applications/enki.desktop
sudo rm -f /usr/share/icons/hicolor/256x256/apps/enki.png /usr/share/icons/hicolor/512x512/apps/enki.png
echo "Enki desinstalado."
UNINSTALL

chmod 755 "$TARBALL_ROOT/install.sh" "$TARBALL_ROOT/uninstall.sh"

TARBALL_FILE="$OUTPUT_DIR/enki-${VERSION}-linux-${DEB_ARCH}.tar.gz"
tar -czf "$TARBALL_FILE" -C "$TARBALL_ROOT" .
echo "==> $TARBALL_FILE"

echo "==> listo"
