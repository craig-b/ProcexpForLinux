# Building and releasing

## Build

```sh
dotnet build ProcexpLinux.slnx
dotnet test ProcexpLinux.slnx
```

Prove the data layer against the live kernel, without a GUI:

```sh
dotnet run --project src/Procexp.Smoke
```

The smoke checker is the fastest way to tell whether a change broke something
real. It reads the machine it runs on and cross-checks against `ps`, `ss`,
`readelf`, `sha256sum`, `nproc` and `/proc/meminfo`, so a parser regression
surfaces as a failed comparison rather than as a wrong number in the UI.

## Release artifacts

```sh
./Scripts/build-release.sh
```

Produces `artifacts/procexp-<version>-linux-x64.tar.gz` containing a staged
filesystem tree, plus the three binaries separately.

Builds are **self-contained**. A system monitor is what you reach for when a
machine is misbehaving, and discovering you first need a matching .NET runtime is
a poor time to find a dependency. The cost is roughly 40 MB for the app and 38 MB
for the helper.

Cross-building for another architecture:

```sh
RID=linux-arm64 ./Scripts/build-release.sh
```

## What ships where

| Path | Contents |
|---|---|
| `/usr/bin/procexp` | The application |
| `/usr/bin/procexp-smoke` | Headless data-layer checker |
| `/usr/lib/procexp/procexp-helper` | Privileged helper — deliberately outside PATH |
| `/usr/lib/systemd/system/procexp-helper.service` | Helper unit, not enabled |
| `/usr/share/applications/procexp.desktop` | Desktop entry |
| `/usr/share/icons/hicolor/scalable/apps/procexp.svg` | Icon |

The helper sits outside `PATH` on purpose. It is started by systemd and never by
a user, and offering it as a command invites someone to run it expecting a window.

## The helper is not enabled by default

Packaging installs the helper binary and its unit but does **not** enable the
service or create the `procexp` group. Membership of that group lets a user read
the environment of any process on the machine — which routinely holds API tokens
and passwords — so it is an administrator's decision, not a packaging default.

See [HELPER.md](HELPER.md) before enabling it.

## Versioning

`Scripts/build-release.sh` takes the version from `git describe --tags --always
--dirty`, so a tagged commit produces a clean version and anything else is
identifiable as a development build.

To cut a release:

```sh
git tag -a v0.1.0 -m "0.1.0"
./Scripts/build-release.sh
```

## CI

`.github/workflows/ci.yml` builds, tests, runs the smoke checker against the
runner's own `/proc`, and uploads the tarball.

Running the smoke checker in CI is deliberate rather than redundant. A container's
`/proc` differs from a desktop's in ways that catch assumptions — far fewer
processes, no login session, restricted cgroups, often no GPU — so it exercises
the degraded paths that a developer machine never hits.
