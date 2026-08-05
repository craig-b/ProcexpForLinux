#!/usr/bin/env bash
#
# Build musl (Alpine) binaries, inside an Alpine container.
#
# This exists as a separate script because cross-compiling musl from a glibc host
# does not work and does not say so. `dotnet publish -r linux-musl-x64` on Arch or
# Debian completes successfully and emits a binary whose ELF interpreter is
# musl's while its shared-library dependencies are still glibc's:
#
#   interpreter /lib/ld-musl-x86_64.so.1 ... needed by ld-linux-x86-64.so.2
#
# It runs on neither libc. Native AOT links real object code, so it needs a
# linker and sysroot for the target, not just the .NET runtime pack — building in
# an Alpine container is the straightforward way to get both.
#
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${ROOT}/artifacts/musl"
IMAGE="mcr.microsoft.com/dotnet/sdk:10.0-alpine"

# linux-musl-x64 or linux-musl-arm64; the container must match, so on a
# different architecture run this on hardware (or emulation) of that kind.
RID="${RID:-linux-musl-x64}"

# Stamped into the binaries so the About window can say which build it is.
# Computed here because the container has no git.
VERSION="$(git -C "${ROOT}" describe --tags --always --dirty 2> /dev/null || echo dev)"

command -v docker > /dev/null 2>&1 || {
  echo "docker is required to build musl binaries from a non-Alpine host." >&2
  echo "On Alpine itself, use Scripts/build-release.sh with RID=linux-musl-x64." >&2
  exit 1
}

rm -rf "${OUT}"
mkdir -p "${OUT}"

docker run --rm \
  -v "${ROOT}:/src:ro" \
  -v "${OUT}:/out" \
  -e RID="${RID}" \
  -e VERSION="${VERSION}" \
  "${IMAGE}" sh -c '
        set -e
        apk add --no-cache clang build-base zlib-dev >/dev/null

        # Copy rather than build in place: the source mount is read-only, and
        # obj/ from a glibc build would otherwise confuse the restore.
        cp -r /src /work && cd /work
        rm -rf artifacts

        for p in Procexp.App:procexp Procexp.Helper:procexp-helper Procexp.Smoke:procexp-smoke; do
            project="${p%%:*}"
            name="${p##*:}"
            echo "==> ${name} (${RID})"
            dotnet publish "src/${project}" \
                --configuration Release \
                --runtime "${RID}" \
                -p:PublishAot=true \
                -p:StripSymbols=true \
                -p:InvariantGlobalization=true \
                -p:InformationalVersion="${VERSION}" \
                --output "/out/${name}" \
                --nologo >/dev/null
        done
    '

# Staging runs on the host — it only copies files, so it needs no musl anything.
"${ROOT}/Scripts/stage-tarball.sh" "${OUT}" "${RID}"

echo "Verify with:"
echo "  docker run --rm -v ${OUT}/procexp-smoke/procexp-smoke:/s:ro alpine:3 /s"
