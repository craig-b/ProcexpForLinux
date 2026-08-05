#!/bin/sh
#
# Bootstrap installer, made to be piped:
#
#   curl -fsSL https://raw.githubusercontent.com/craig-b/ProcexpForLinux/main/Scripts/get.sh | sh
#
# Flags after `sh -s --` go to the installer, e.g.:
#
#   ... | sh -s -- --enable-helper
#
# Detects architecture and libc, downloads the matching release tarball,
# verifies its checksum, and runs Scripts/install.sh against it under sudo.
# POSIX sh throughout, because a pipe runs under sh — often dash or busybox —
# not bash. Environment overrides: PROCEXP_VERSION pins a tag (default:
# latest release); PROCEXP_REPO redirects to a fork.
#
set -eu

REPO="${PROCEXP_REPO:-craig-b/ProcexpForLinux}"
VERSION="${PROCEXP_VERSION:-}"

die() {
  printf 'get-procexp: %s\n' "$*" >&2
  exit 1
}

# --- What machine is this? --------------------------------------------------

[ "$(uname -s)" = Linux ] || die "this installs a Linux program (uname says $(uname -s))"
[ -r /proc/self/status ] || die "/proc is not mounted, which the program requires"

case "$(uname -m)" in
  x86_64) arch=x64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) die "unsupported architecture $(uname -m) — releases cover x86_64 and aarch64" ;;
esac

# A musl loader means the glibc build cannot run here (and vice versa), so the
# variant is chosen by what the machine can actually execute.
if [ -e "/lib/ld-musl-$(uname -m).so.1" ]; then
  rid="linux-musl-${arch}"
else
  rid="linux-${arch}"
fi

# --- What this script needs -------------------------------------------------

fetch() {
  if command -v curl > /dev/null 2>&1; then
    curl -fsSL -o "$2" "$1"
  elif command -v wget > /dev/null 2>&1; then
    wget -qO "$2" "$1"
  else
    die "curl or wget is required"
  fi
}

command -v tar > /dev/null 2>&1 || die "tar is required"
command -v sha256sum > /dev/null 2>&1 || die "sha256sum is required"
command -v bash > /dev/null 2>&1 \
  || die "bash is required by the installer (on Alpine: apk add bash)"

if [ "$(id -u)" != 0 ]; then
  command -v sudo > /dev/null 2>&1 \
    || die "installing into /usr needs root — run as root or install sudo"
fi

# --- Download and verify ----------------------------------------------------

if [ -z "${VERSION}" ]; then
  VERSION="$(
    fetch "https://api.github.com/repos/${REPO}/releases/latest" - \
      | sed -n 's/.*"tag_name" *: *"\([^"]*\)".*/\1/p' \
      | head -n 1
  )"
  [ -n "${VERSION}" ] || die "could not determine the latest release of ${REPO}"
fi

tarball="procexp-${VERSION}-${rid}.tar.gz"
base="https://github.com/${REPO}/releases/download/${VERSION}"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM

printf 'Downloading %s (%s)...\n' "${tarball}" "${VERSION}"
fetch "${base}/${tarball}" "${tmp}/${tarball}"
fetch "${base}/SHA256SUMS" "${tmp}/SHA256SUMS"

(
  cd "${tmp}" \
    && grep " ${tarball}\$" SHA256SUMS | sha256sum -c - > /dev/null
) || die "checksum verification failed for ${tarball}"
printf 'Checksum verified.\n'

# --- Hand off to the real installer -----------------------------------------

# Newer tarballs carry the installer; fall back to fetching it from the
# repository for releases that predate that. PROCEXP_INSTALLER points at a
# local copy, for testing this script without a published release.
installer="${PROCEXP_INSTALLER:-}"
if [ -z "${installer}" ]; then
  installer="${tmp}/install.sh"
  if tar -xzf "${tmp}/${tarball}" -C "${tmp}" ./usr/share/procexp/install.sh 2> /dev/null; then
    mv "${tmp}/usr/share/procexp/install.sh" "${installer}"
  else
    fetch "https://raw.githubusercontent.com/${REPO}/main/Scripts/install.sh" "${installer}"
  fi
fi

if [ "$(id -u)" = 0 ]; then
  bash "${installer}" --tarball "${tmp}/${tarball}" "$@"
else
  printf 'Installing needs root; sudo will ask for your password.\n'
  sudo bash "${installer}" --tarball "${tmp}/${tarball}" "$@"
fi
