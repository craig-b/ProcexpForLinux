#!/usr/bin/env bash
#
# Build native release binaries.
#
# Self-contained rather than framework-dependent: a system monitor is exactly the
# tool you reach for when a machine is misbehaving, and needing a matching .NET
# runtime installed first is a poor time to discover a dependency.
#
# Native AOT rather than a single-file IL bundle. Measured on this codebase:
#
#             on disk    RSS     PSS    startup
#   bundle     42 MB    314 MB  181 MB   83 ms
#   AOT        25 MB    234 MB  101 MB    3 ms
#
# The startup figure matters more than it looks: the helper is spawned by systemd
# and the smoke checker is run from scripts, so an 80 ms floor on every
# invocation is pure overhead. AOT needs clang and a linker, which the build
# environment must provide — see docs/RELEASE.md.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${ROOT}/artifacts"
RID="${RID:-linux-x64}"

rm -rf "${OUT}"
mkdir -p "${OUT}"

publish() {
    local project="$1" name="$2"

    echo "==> ${name} (${RID})"

    dotnet publish "${ROOT}/src/${project}" \
        --configuration Release \
        --runtime "${RID}" \
        -p:PublishAot=true \
        -p:StripSymbols=true \
        -p:InvariantGlobalization=true \
        --output "${OUT}/${name}" \
        --nologo
}

publish Procexp.App procexp
publish Procexp.Helper procexp-helper
publish Procexp.Smoke procexp-smoke

# Flatten the three binaries into one staging tree.
STAGE="${OUT}/stage"
mkdir -p "${STAGE}/usr/bin" "${STAGE}/usr/lib/procexp" \
         "${STAGE}/usr/share/applications" "${STAGE}/usr/share/icons/hicolor/scalable/apps" \
         "${STAGE}/usr/lib/systemd/system"

install -Dm755 "${OUT}/procexp/procexp"               "${STAGE}/usr/bin/procexp"
install -Dm755 "${OUT}/procexp-smoke/procexp-smoke"    "${STAGE}/usr/bin/procexp-smoke"

# The helper lives outside PATH: it is started by systemd, never by a user, and
# offering it as a command invites someone to run it expecting a UI.
install -Dm755 "${OUT}/procexp-helper/procexp-helper"  "${STAGE}/usr/lib/procexp/procexp-helper"

install -Dm644 "${ROOT}/packaging/procexp.desktop"         "${STAGE}/usr/share/applications/procexp.desktop"
install -Dm644 "${ROOT}/packaging/procexp.svg"             "${STAGE}/usr/share/icons/hicolor/scalable/apps/procexp.svg"
install -Dm644 "${ROOT}/packaging/procexp-helper.service"  "${STAGE}/usr/lib/systemd/system/procexp-helper.service"

VERSION="$(git -C "${ROOT}" describe --tags --always --dirty 2>/dev/null || echo dev)"
TARBALL="${OUT}/procexp-${VERSION}-${RID}.tar.gz"

tar -czf "${TARBALL}" -C "${STAGE}" .

echo
echo "Binaries:  ${OUT}/procexp/procexp"
echo "Tarball:   ${TARBALL}"
echo
echo "The helper is optional. Installing it grants members of the 'procexp'"
echo "group the ability to read any process's environment — see docs/HELPER.md"
echo "before enabling it."
