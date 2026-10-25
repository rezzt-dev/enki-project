#!/usr/bin/env bash
# ==============================================================================
#  Enki · instalador para Linux
#
#  Compila Enki (Release, autocontenido) y lo instala o actualiza por usuario,
#  sin privilegios de administrador y reutilizando la instalación existente:
#
#    ~/.local/share/enki                          ejecutable + librerías nativas
#    ~/.local/bin/enki                            comando "enki" (enlace simbólico)
#    ~/.local/share/applications/enki.desktop     entrada del menú de aplicaciones
#    ~/.local/share/icons/hicolor/*/apps/enki.png iconos
#
#  Los datos del usuario (~/.config/Enki) nunca se tocan.
#
#  Uso: scripts/install-linux.sh [opciones]
#    --rid <RID>      Runtime de destino (por defecto, el de esta máquina)
#    --prefix <DIR>   Carpeta de instalación (por defecto ~/.local/share/enki)
#    --skip-build     Reutiliza la última publicación en ./publish/<RID>
#    --uninstall      Elimina la aplicación (conserva los datos del usuario)
#    --verbose        Muestra la salida completa de dotnet
#    --no-color       Desactiva colores y símbolos Unicode
#    -h, --help       Muestra esta ayuda
# ==============================================================================

set -Eeuo pipefail

# ── Rutas y valores por defecto ───────────────────────────────────────────────
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ="$REPO_ROOT/enki/src/enki/enki.csproj"
PROPS="$REPO_ROOT/enki/Directory.Build.props"
ICON_SRC="$REPO_ROOT/enki/src/enki/assets/icon"
DESKTOP_SRC="$REPO_ROOT/enki/packaging/linux/enki.desktop"

DATA_HOME="${XDG_DATA_HOME:-$HOME/.local/share}"
INSTALL_DIR="$DATA_HOME/enki"
BIN_DIR="$HOME/.local/bin"
APPS_DIR="$DATA_HOME/applications"
ICONS_DIR="$DATA_HOME/icons/hicolor"
ICON_SIZES=(256 512 1024)
MIN_FREE_MB=1024

case "$(uname -m)" in
  aarch64|arm64) RID="linux-arm64" ;;
  *)             RID="linux-x64" ;;
esac
SKIP_BUILD=0; UNINSTALL=0; VERBOSE=0; COLOR=1

# ── Argumentos ────────────────────────────────────────────────────────────────
usage() { sed -n '3,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }
while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)        RID="${2:?--rid requiere un valor}"; shift 2 ;;
    --prefix)     INSTALL_DIR="${2:?--prefix requiere un valor}"; shift 2 ;;
    --skip-build) SKIP_BUILD=1; shift ;;
    --uninstall)  UNINSTALL=1; shift ;;
    --verbose)    VERBOSE=1; shift ;;
    --no-color)   COLOR=0; shift ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "Opción desconocida: $1 (usa --help)" >&2; exit 2 ;;
  esac
done
INSTALL_DIR="${INSTALL_DIR%/}"
[[ -n "$INSTALL_DIR" && "$INSTALL_DIR" != "$HOME" ]] || { echo "Ruta de instalación no válida." >&2; exit 2; }

# ── Estilo de salida ──────────────────────────────────────────────────────────
if [[ $COLOR -eq 1 && -t 1 && -z "${NO_COLOR:-}" ]]; then
  B=$'\e[1m'; D=$'\e[2m'; R=$'\e[0m'
  RED=$'\e[31m'; GRN=$'\e[32m'; YEL=$'\e[33m'; CYN=$'\e[36m'
  OK="✔"; KO="✖"; WARN="!"; DOT="•"; ARROW="→"; RULE="──────────────────────────────────────────────"
else
  B=""; D=""; R=""; RED=""; GRN=""; YEL=""; CYN=""
  OK="OK"; KO="ERROR"; WARN="AVISO"; DOT="-"; ARROW="->"; RULE="----------------------------------------------"
  COLOR=0
fi

TOTAL_STEPS=0; STEP=0
step() { STEP=$((STEP + 1)); printf '\n%s[%d/%d]%s %s%s%s\n' "$CYN$B" "$STEP" "$TOTAL_STEPS" "$R" "$B" "$1" "$R"; }
ok()   { printf '  %s%s%s %s\n' "$GRN" "$OK" "$R" "$1"; }
info() { printf '  %s%s%s %s\n' "$D" "$DOT" "$R" "$1"; }
warn() { printf '  %s%s%s %s\n' "$YEL" "$WARN" "$R" "$1"; }
die()  { printf '\n%s%s %s%s\n' "$RED$B" "$KO" "$1" "$R" >&2; exit 1; }
banner() { printf '\n  %sEnki%s · %s\n  %s%s%s\n' "$B" "$R" "$1" "$D" "$RULE" "$R"; }

# ── Utilidades ────────────────────────────────────────────────────────────────
WORK_DIR=""; STAGE=""; OLD=""; SWAPPED=0; KEEP_LOG=0; LOG_FILE=""
START_TS=$SECONDS

human_size()  { du -sh "$1" 2>/dev/null | cut -f1; }
app_version() { sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROPS" 2>/dev/null | head -n1; }
elapsed()     { local s=$((SECONDS - START_TS)); printf '%dm %02ds' $((s / 60)) $((s % 60)); }
enki_running() { pgrep -u "$UID" -x enki >/dev/null 2>&1; }

# Ejecuta un comando mostrando un indicador de progreso; el detalle va a $LOG_FILE.
run_with_spinner() {
  local label="$1"; shift
  if [[ $VERBOSE -eq 1 ]]; then
    "$@" 2>&1 | tee -a "$LOG_FILE"; return "${PIPESTATUS[0]}"
  fi
  "$@" >>"$LOG_FILE" 2>&1 &
  local pid=$! i=0 t0=$SECONDS
  local frames=(⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏)
  if [[ $COLOR -eq 0 || ! -t 1 ]]; then
    printf '  %s %s...\n' "$DOT" "$label"; wait "$pid"; return $?
  fi
  while kill -0 "$pid" 2>/dev/null; do
    printf '\r  %s%s%s %s %s(%ds)%s ' "$CYN" "${frames[i++ % ${#frames[@]}]}" "$R" "$label" "$D" "$((SECONDS - t0))" "$R"
    sleep 0.1
  done
  printf '\r\033[K'
  wait "$pid"
}

# Si ya se había intercambiado la carpeta, restaura la instalación anterior.
rollback() {
  if [[ $SWAPPED -eq 1 && -n "$OLD" && -d "$OLD" ]]; then
    rm -rf "${INSTALL_DIR:?}"
    mv "$OLD" "$INSTALL_DIR" && warn "Instalación anterior restaurada." >&2
    OLD=""; SWAPPED=0
  fi
}

on_error() {
  local code=$?
  trap - ERR
  printf '\n%s%s La instalación ha fallado (código %d).%s\n' "$RED$B" "$KO" "$code" "$R" >&2
  rollback
  if [[ -n "$LOG_FILE" && -s "$LOG_FILE" ]]; then
    printf '\n%sÚltimas líneas del registro:%s\n' "$B" "$R" >&2
    tail -n 25 "$LOG_FILE" | sed 's/^/    /' >&2
    KEEP_LOG=1
  fi
  exit "$code"
}

cleanup() {
  [[ -n "$STAGE" && -d "$STAGE" ]] && rm -rf "${STAGE:?}"
  if [[ -n "$WORK_DIR" && -d "$WORK_DIR" ]]; then
    if [[ $KEEP_LOG -eq 1 && -f "$LOG_FILE" ]]; then
      mkdir -p "$HOME/.cache" && cp "$LOG_FILE" "$HOME/.cache/enki-install.log" \
        && printf '\n  Registro completo: %s\n' "$HOME/.cache/enki-install.log" >&2
    fi
    rm -rf "${WORK_DIR:?}"
  fi
}
trap on_error ERR
trap cleanup EXIT
trap 'printf "\n"; warn "Interrumpido por el usuario."; rollback; exit 130' INT TERM

# ¿El enlace $1 apunta ya a nuestra instalación?
points_here() {
  [[ -L "$1" && "$(readlink -f "$1" 2>/dev/null || true)" == "$(readlink -f "$INSTALL_DIR/enki" 2>/dev/null || true)" ]]
}

refresh_desktop() {
  if command -v update-desktop-database >/dev/null 2>&1; then update-desktop-database "$APPS_DIR" >/dev/null 2>&1 || true; fi
  if command -v gtk-update-icon-cache  >/dev/null 2>&1; then gtk-update-icon-cache -q -t "$ICONS_DIR" >/dev/null 2>&1 || true; fi
  if command -v kbuildsycoca6          >/dev/null 2>&1; then kbuildsycoca6 >/dev/null 2>&1 || true; fi
}

# ── Desinstalación ────────────────────────────────────────────────────────────
do_uninstall() {
  TOTAL_STEPS=2
  banner "Desinstalación"
  [[ -d "$INSTALL_DIR" ]] || die "No hay ninguna instalación en $INSTALL_DIR."

  step "Cerrando Enki"
  if enki_running; then pkill -u "$UID" -x enki || true; sleep 1; ok "Proceso detenido"; else info "No estaba en ejecución"; fi

  step "Eliminando archivos"
  if points_here "$BIN_DIR/enki"; then rm -f "${BIN_DIR:?}/enki"; ok "Comando: $BIN_DIR/enki"; fi
  if [[ -f "$APPS_DIR/enki.desktop" ]] && grep -q '^Exec=enki' "$APPS_DIR/enki.desktop"; then
    rm -f "${APPS_DIR:?}/enki.desktop"; ok "Menú: $APPS_DIR/enki.desktop"
  fi
  for size in "${ICON_SIZES[@]}"; do rm -f "${ICONS_DIR:?}/${size:?}x${size:?}/apps/enki.png"; done
  ok "Iconos"
  rm -rf "${INSTALL_DIR:?}"; ok "Aplicación: $INSTALL_DIR"
  refresh_desktop
  printf '\n%s%s Enki se ha desinstalado.%s\n  Tus datos se conservan en %s~/.config/Enki%s\n\n' "$GRN$B" "$OK" "$R" "$B" "$R"
}

if [[ $UNINSTALL -eq 1 ]]; then do_uninstall; exit 0; fi

# ── Instalación ───────────────────────────────────────────────────────────────
TOTAL_STEPS=6
VERSION="$(app_version)"; VERSION="${VERSION:-desconocida}"
banner "Instalación · versión $VERSION · $RID"

# 1 ── Comprobaciones previas
step "Comprobando requisitos"
command -v dotnet >/dev/null 2>&1 || die "No se encuentra 'dotnet'. Instala el SDK de .NET 8 o superior (Arch: sudo pacman -S dotnet-sdk)."
DOTNET_VER="$(dotnet --version 2>/dev/null || echo 0)"
[[ "${DOTNET_VER%%.*}" -ge 8 ]] || die "Se requiere el SDK de .NET 8 o superior (detectado: $DOTNET_VER)."
ok "SDK de .NET $DOTNET_VER"
[[ -f "$CSPROJ" ]] || die "No se encuentra el proyecto: $CSPROJ"
ok "Proyecto localizado"
mkdir -p "$(dirname "$INSTALL_DIR")"
FREE_MB="$(df -Pm "$(dirname "$INSTALL_DIR")" | awk 'NR==2{print $4}')"
[[ "$FREE_MB" -ge $MIN_FREE_MB ]] || die "Espacio libre insuficiente: ${FREE_MB} MB (se necesitan ${MIN_FREE_MB} MB)."
ok "Espacio libre: ${FREE_MB} MB"
if [[ -d "$INSTALL_DIR" ]]; then
  info "Instalación existente en $INSTALL_DIR ($(human_size "$INSTALL_DIR")) $ARROW se actualizará"
else
  info "Instalación nueva en $INSTALL_DIR"
fi

WORK_DIR="$(mktemp -d)"; LOG_FILE="$WORK_DIR/build.log"; : >"$LOG_FILE"
STAGE="$(mktemp -d "${INSTALL_DIR}.new.XXXXXX")"

# 2 ── Compilación
step "Compilando Enki"
if [[ $SKIP_BUILD -eq 0 ]]; then
  run_with_spinner "dotnet publish ($RID, Release)" \
    dotnet publish "$CSPROJ" -c Release -r "$RID" --self-contained true \
      -p:PublishSingleFile=true -p:PublishTrimmed=false -o "$STAGE" -nologo -v:minimal
  ok "Compilación completada ($(elapsed))"
else
  [[ -d "$REPO_ROOT/publish/$RID" ]] || die "--skip-build: no existe $REPO_ROOT/publish/$RID"
  cp -a "$REPO_ROOT/publish/$RID/." "$STAGE/"
  ok "Reutilizando publish/$RID"
fi

# 3 ── Verificación
step "Verificando el paquete"
[[ -x "$STAGE/enki" ]]             || die "La publicación no contiene el ejecutable 'enki'."
ok "Ejecutable ($(human_size "$STAGE/enki"))"
[[ -f "$STAGE/polar.properties" ]] || die "Falta polar.properties (configura enki/local.properties o SUPABASE_URL/SUPABASE_ANON_KEY)."
ok "Configuración pública de Polar"
rm -f "${STAGE:?}/local.properties"
ok "Sin credenciales locales en el paquete"
chmod 755 "$STAGE"

# 4 ── Instalación
step "Instalando"
if enki_running; then warn "Enki está en ejecución: ciérralo y ábrelo de nuevo para usar la versión nueva."; fi
if [[ -e "$INSTALL_DIR" ]]; then OLD="${INSTALL_DIR}.old.$$"; mv "$INSTALL_DIR" "$OLD"; fi
SWAPPED=1; mv "$STAGE" "$INSTALL_DIR"; STAGE=""
ok "Archivos instalados en $INSTALL_DIR ($(human_size "$INSTALL_DIR"))"

# 5 ── Integración con el sistema
step "Integrando con el escritorio"
mkdir -p "$BIN_DIR" "$APPS_DIR"
if points_here "$BIN_DIR/enki"; then
  info "Comando 'enki' ya apuntaba aquí"
else
  ln -sfn "$INSTALL_DIR/enki" "$BIN_DIR/enki"; ok "Comando: $BIN_DIR/enki $ARROW $INSTALL_DIR/enki"
fi
if [[ -f "$APPS_DIR/enki.desktop" ]]; then
  info "Entrada de menú existente conservada"
else
  cp "$DESKTOP_SRC" "$APPS_DIR/enki.desktop"; ok "Entrada de menú creada"
fi
added=0
for size in "${ICON_SIZES[@]}"; do
  dest="$ICONS_DIR/${size}x${size}/apps/enki.png"
  if [[ ! -f "$dest" && -f "$ICON_SRC/enki-$size.png" ]]; then
    mkdir -p "$(dirname "$dest")"; cp "$ICON_SRC/enki-$size.png" "$dest"; added=$((added + 1))
  fi
done
if [[ $added -gt 0 ]]; then ok "$added icono(s) añadido(s)"; else info "Iconos ya presentes"; fi
refresh_desktop
case ":$PATH:" in *":$BIN_DIR:"*) ;; *) warn "$BIN_DIR no está en tu PATH; añádelo para usar el comando 'enki'." ;; esac

# 6 ── Limpieza
step "Finalizando"
if [[ -n "$OLD" ]]; then rm -rf "${OLD:?}"; OLD=""; SWAPPED=0; ok "Versión anterior eliminada"; fi
ok "Instalación completada"

printf '\n%s%s Enki %s instalado correctamente%s en %s\n' "$GRN$B" "$OK" "$VERSION" "$R" "$(elapsed)"
printf '\n  %sEjecutar%s   enki   %s(o desde el menú de aplicaciones)%s\n' "$B" "$R" "$D" "$R"
printf '  %sInstalado%s  %s\n' "$B" "$R" "$INSTALL_DIR"
printf '  %sDatos%s      ~/.config/Enki %s(sin modificar)%s\n' "$B" "$R" "$D" "$R"
printf '  %sQuitar%s     scripts/install-linux.sh --uninstall\n\n' "$B" "$R"
