#!/usr/bin/env bash
#
# Stage published binaries into a filesystem tree and produce the release
# tarball. Shared by build-release.sh (native build) and build-musl.sh
# (container build): the installed tree must be identical whichever path built
# the binaries, and one copy of this list means it cannot drift.
#
# Usage: stage-tarball.sh BINDIR RID
#   BINDIR  directory containing procexp/, procexp-helper/ and procexp-smoke/
#   RID     runtime identifier for the tarball name, e.g. linux-x64
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BINDIR="${1:?usage: stage-tarball.sh BINDIR RID}"
RID="${2:?usage: stage-tarball.sh BINDIR RID}"

STAGE="${BINDIR}/stage"
rm -rf "${STAGE}"
mkdir -p "${STAGE}/usr/bin" "${STAGE}/usr/lib/procexp" \
  "${STAGE}/usr/share/applications" "${STAGE}/usr/share/icons/hicolor/scalable/apps" \
  "${STAGE}/usr/lib/systemd/system"

# Native AOT emits one executable, but Avalonia's renderer does not follow:
# SkiaSharp and HarfBuzz ship as shared libraries that must sit next to the
# real binary. So the GUI lives in /usr/lib/procexp with its libraries and
# /usr/bin/procexp is a symlink — the loader resolves /proc/self/exe through
# the link, so the libraries are found beside the target.
install -Dm755 "${BINDIR}/procexp/procexp" "${STAGE}/usr/lib/procexp/procexp"
for lib in "${BINDIR}/procexp/"*.so; do
  install -Dm755 "${lib}" "${STAGE}/usr/lib/procexp/$(basename "${lib}")"
done
ln -s ../lib/procexp/procexp "${STAGE}/usr/bin/procexp"

install -Dm755 "${BINDIR}/procexp-smoke/procexp-smoke" "${STAGE}/usr/bin/procexp-smoke"

# The helper lives outside PATH: it is started by systemd, never by a user, and
# offering it as a command invites someone to run it expecting a UI.
install -Dm755 "${BINDIR}/procexp-helper/procexp-helper" "${STAGE}/usr/lib/procexp/procexp-helper"

# The installer travels with the tree, so an installed system can uninstall
# or activate the helper later without the repository, and the curl-pipe
# bootstrap (Scripts/get.sh) runs the installer that matches the tarball.
install -Dm755 "${ROOT}/Scripts/install.sh" "${STAGE}/usr/share/procexp/install.sh"

install -Dm644 "${ROOT}/packaging/procexp.desktop" "${STAGE}/usr/share/applications/procexp.desktop"
install -Dm644 "${ROOT}/packaging/procexp.svg" "${STAGE}/usr/share/icons/hicolor/scalable/apps/procexp.svg"
install -Dm644 "${ROOT}/packaging/procexp-helper.service" "${STAGE}/usr/lib/systemd/system/procexp-helper.service"

# The GUI is unrunnable without its renderer, and nothing else in the pipeline
# executes it (CI verifies the smoke checker, which needs no display) — so the
# one place that can catch a missing library is here, structurally.
[[ -f "${STAGE}/usr/lib/procexp/libSkiaSharp.so" ]] || {
  echo "libSkiaSharp.so missing from the publish output — the GUI cannot start without it." >&2
  exit 1
}

VERSION="$(git -C "${ROOT}" describe --tags --always --dirty 2> /dev/null || echo dev)"
TARBALL="${ROOT}/artifacts/procexp-${VERSION}-${RID}.tar.gz"

# Recorded so the bootstrap can answer "is this machine already up to date?"
# without downloading anything. A tagged build writes the tag itself; dev
# builds write something that never equals a tag, so they always reinstall.
mkdir -p "${STAGE}/usr/share/procexp"
printf '%s\n' "${VERSION}" > "${STAGE}/usr/share/procexp/version"

# Owned by root in the archive, not by whoever ran the build: this tree is
# extracted into /, and a user-owned /usr/lib/procexp/procexp-helper — a binary
# systemd runs as root — would be a privilege escalation.
tar -czf "${TARBALL}" -C "${STAGE}" --owner=0 --group=0 --numeric-owner .

echo
echo "Tarball:   ${TARBALL}"
echo
echo "Install with Scripts/install.sh. The helper is optional: activating it"
echo "grants members of the 'procexp' group the ability to read any process's"
echo "environment — see docs/HELPER.md before enabling it."
