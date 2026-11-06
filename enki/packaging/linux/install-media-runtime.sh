#!/usr/bin/env bash
# Instala el motor y los plugins mediante los repositorios ya configurados del sistema.
# Se ejecuta antes de copiar Enki: si falla, la instalación no anuncia un éxito incompleto.
set -euo pipefail

select_media_packages() {
  local distribution
  MEDIA_MANAGER=""
  MEDIA_PACKAGES=()
  for distribution in "$1" ${2:-}; do
    case "$distribution" in
      debian|ubuntu|linuxmint|pop)
        MEDIA_MANAGER=apt-get
        MEDIA_PACKAGES=(libvlc5 vlc-plugin-base vlc-plugin-video-output)
        return 0 ;;
      arch|manjaro|endeavouros|xerolinux|XeroLinux)
        MEDIA_MANAGER=pacman
        # En Arch, libvlc por sí solo no incluye FFmpeg ni una salida gráfica.
        MEDIA_PACKAGES=(libvlc vlc-plugins-all)
        return 0 ;;
      fedora|rhel|centos)
        MEDIA_MANAGER=dnf
        MEDIA_PACKAGES=(vlc)
        return 0 ;;
      opensuse*|suse|sles)
        MEDIA_MANAGER=zypper
        MEDIA_PACKAGES=(vlc)
        return 0 ;;
    esac
  done
  return 1
}

install_media_runtime() {
  if [[ ! -r /etc/os-release ]]; then
    echo "No se puede identificar el sistema para preparar la reproducción multimedia." >&2
    return 1
  fi
  # Fuente estándar del propio sistema, nunca del archivo descargado de Enki.
  local ID="" ID_LIKE=""
  source /etc/os-release
  if ! select_media_packages "$ID" "${ID_LIKE:-}"; then
    echo "El instalador de Enki todavía no admite esta distribución: $ID." >&2
    return 1
  fi
  install_selected_media_runtime
}

install_selected_media_runtime() {
  if ! command -v "$MEDIA_MANAGER" >/dev/null 2>&1; then
    echo "No se encuentra el gestor de paquetes $MEDIA_MANAGER." >&2
    return 1
  fi
  local elevate=()
  if [[ "$EUID" -ne 0 ]]; then elevate=(sudo); fi
  echo "==> Preparando automáticamente la reproducción de audio y vídeo de Enki"
  case "$MEDIA_MANAGER" in
    apt-get)
      "${elevate[@]}" apt-get update
      "${elevate[@]}" apt-get install -y --no-install-recommends "${MEDIA_PACKAGES[@]}" ;;
    pacman)
      # No sincronizar bases aisladas (-Sy) ni forzar una actualización completa del sistema.
      "${elevate[@]}" pacman -S --needed --noconfirm "${MEDIA_PACKAGES[@]}" ;;
    dnf) "${elevate[@]}" dnf install -y "${MEDIA_PACKAGES[@]}" ;;
    zypper) "${elevate[@]}" zypper --non-interactive install --no-recommends "${MEDIA_PACKAGES[@]}" ;;
  esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  install_media_runtime
fi
