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

# Stamped into the binaries so the About window can say which build it is.
VERSION="$(git -C "${ROOT}" describe --tags --always --dirty 2> /dev/null || echo dev)"

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
    -p:InformationalVersion="${VERSION}" \
    --output "${OUT}/${name}" \
    --nologo
}

publish Procexp.App procexp
publish Procexp.Helper procexp-helper
publish Procexp.Smoke procexp-smoke

# Staging and the tarball are shared with the musl build, so the installed
# tree is identical whichever script produced the binaries.
"${ROOT}/Scripts/stage-tarball.sh" "${OUT}" "${RID}"
