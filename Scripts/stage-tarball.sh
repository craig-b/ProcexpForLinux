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

install -Dm755 "${BINDIR}/procexp/procexp" "${STAGE}/usr/bin/procexp"
install -Dm755 "${BINDIR}/procexp-smoke/procexp-smoke" "${STAGE}/usr/bin/procexp-smoke"

# The helper lives outside PATH: it is started by systemd, never by a user, and
# offering it as a command invites someone to run it expecting a UI.
install -Dm755 "${BINDIR}/procexp-helper/procexp-helper" "${STAGE}/usr/lib/procexp/procexp-helper"

install -Dm644 "${ROOT}/packaging/procexp.desktop" "${STAGE}/usr/share/applications/procexp.desktop"
install -Dm644 "${ROOT}/packaging/procexp.svg" "${STAGE}/usr/share/icons/hicolor/scalable/apps/procexp.svg"
install -Dm644 "${ROOT}/packaging/procexp-helper.service" "${STAGE}/usr/lib/systemd/system/procexp-helper.service"

VERSION="$(git -C "${ROOT}" describe --tags --always --dirty 2> /dev/null || echo dev)"
TARBALL="${ROOT}/artifacts/procexp-${VERSION}-${RID}.tar.gz"

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
