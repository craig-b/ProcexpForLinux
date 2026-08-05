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

The smoke checker is the fastest way to tell whether a change broke something real. It reads the
machine it runs on and cross-checks against `ps`, `ss`, `readelf`, `sha256sum`, `nproc` and
`/proc/meminfo`, so a parser regression surfaces as a failed comparison rather than as a wrong
number in the UI.

## Release artifacts

```sh
./Scripts/build-release.sh
```

Produces `artifacts/procexp-<version>-linux-x64.tar.gz` containing a staged filesystem tree, plus
the three binaries separately.

### Native AOT

Builds are **self-contained and natively compiled**. Self-contained because a system monitor is what
you reach for when a machine is misbehaving, and discovering you first need a matching .NET runtime
is a poor time to find a dependency. Native AOT because it is better on every axis that matters
here.

Measured on this codebase, GUI application:

|                     | Single-file IL bundle | Native AOT |
| ------------------- | --------------------- | ---------- |
| On disk             | 42 MB                 | **25 MB**  |
| Resident memory     | 314 MB                | **234 MB** |
| Proportional memory | 181 MB                | **101 MB** |

And the console tools, where startup dominates:

|                          | Bundle | Native AOT |
| ------------------------ | ------ | ---------- |
| `procexp-helper` on disk | 38 MB  | **3.6 MB** |
| `procexp-smoke` on disk  | 38 MB  | **5.5 MB** |
| Startup to exit          | 83 ms  | **3.2 ms** |

The startup figure matters more than the size. The helper is spawned by systemd and the smoke
checker is run from scripts and CI, so an 80 ms floor on every invocation is pure overhead. Much of
that floor was decompressing the single-file bundle on each start.

Nothing had to be given up for it: the codebase was already AOT-clean apart from one file, because
the helper protocol uses source-generated JSON and every P/Invoke uses the `LibraryImport`
generator. Only `SettingsStore` used reflection serialisation, and it now uses a source-generated
context too.

The trim, AOT and single-file analysers are enabled for **every** project in
`Directory.Build.props`, and warnings are errors. Reflection creeping into a library therefore fails
the ordinary build rather than silently breaking the published one.

### Build requirements for AOT

Native AOT compiles and links real object code, so the build machine needs a C toolchain that
`dotnet` can drive:

```sh
# Arch
sudo pacman -S --needed clang lld

# Debian / Ubuntu
sudo apt install clang zlib1g-dev
```

If those are missing the publish fails with a linker error rather than falling back silently.

### Cross-architecture builds

```sh
RID=linux-arm64 ./Scripts/build-release.sh
```

Note that Native AOT cross-compilation needs a **cross toolchain** for the target architecture, not
just the .NET runtime pack — unlike an IL bundle, which is architecture-neutral until it runs.
Building arm64 on an x64 host means either installing a cross-linker and sysroot, or building on
arm64 hardware. CI does the latter.

## Install

```sh
sudo ./Scripts/install.sh                # from a local build's artifacts/stage
sudo ./Scripts/install.sh --tarball FILE # from a release tarball
sudo ./Scripts/install.sh --uninstall
```

A script rather than a package because the binaries are self-contained: there is no runtime to
resolve and no dependency graph to walk, so installation is copying the staged tree into place. On
Arch, prefer `packaging/PKGBUILD` — a distribution package tracks files and conflicts in ways a
script cannot.

The script records what it installed in `/usr/share/procexp/manifest`, so `--uninstall` removes
exactly that. It will offer to activate the helper — interactively, or with `--enable-helper` /
`--without-helper` / `--add-user NAME` when scripted — but never activates it silently, for the
reasons below.

## What ships where

| Path                                                 | Contents                                      |
| ---------------------------------------------------- | --------------------------------------------- |
| `/usr/bin/procexp`                                   | Symlink to the application                    |
| `/usr/bin/procexp-smoke`                             | Headless data-layer checker                   |
| `/usr/lib/procexp/procexp`                           | The application                               |
| `/usr/lib/procexp/libSkiaSharp.so`, `libHarfBuzz…`   | Avalonia's renderer, loaded beside the binary |
| `/usr/lib/procexp/procexp-helper`                    | Privileged helper — deliberately outside PATH |
| `/usr/lib/systemd/system/procexp-helper.service`     | Helper unit, not enabled                      |
| `/usr/share/applications/procexp.desktop`            | Desktop entry                                 |
| `/usr/share/icons/hicolor/scalable/apps/procexp.svg` | Icon                                          |

The helper sits outside `PATH` on purpose. It is started by systemd and never by a user, and
offering it as a command invites someone to run it expecting a window.

The GUI is _almost_ a single file: Native AOT emits one executable, but Avalonia's renderer —
SkiaSharp and HarfBuzz — ships as native shared libraries that must sit next to it. Hence the
symlink arrangement: the loader resolves `/proc/self/exe` through the link, so the libraries are
found beside the real binary in `/usr/lib/procexp`.

## The helper is not enabled by default

Packaging installs the helper binary and its unit but does **not** enable the service or create the
`procexp` group. Membership of that group lets a user read the environment of any process on the
machine — which routinely holds API tokens and passwords — so it is an administrator's decision, not
a packaging default.

See [HELPER.md](HELPER.md) before enabling it.

## Versioning

`Scripts/build-release.sh` takes the version from `git describe --tags --always --dirty`, so a
tagged commit produces a clean version and anything else is identifiable as a development build.

To cut a release:

```sh
git tag -a v0.1.0 -m "0.1.0"
./Scripts/build-release.sh
```

## Formatting and code style

Two tools, split by concern.

**CSharpier** owns layout — C#, XAML and MSBuild files alike. It is an opinionated formatter with
almost nothing to configure, which is the point: no one argues about where a brace goes.

```sh
dotnet tool restore # once
dotnet csharpier format .
dotnet csharpier check . # what CI runs
```

**Prettier** owns the rest — Markdown, YAML, JSON and, via `prettier-plugin-sh`, the shell scripts
and `PKGBUILD`. Prose is reflowed rather than preserved: hand-wrapping is a decision nobody should
have to keep making, and leaving it to each author is the drift a formatter exists to remove.

```sh
npm ci # once
npx prettier --write .
npx prettier --check . # what CI runs
```

Node is a formatting-only dependency; there is no JavaScript in this project. The shell plugin uses
the same engine as `shfmt`, so it needs no separate toolchain.

Only three things are formatted by nobody — systemd units, desktop entries and `app.manifest` —
because Prettier has no parser for them and fails rather than skipping.

**`.editorconfig`** owns everything CSharpier has no opinion about: naming, using ordering, `var`
usage, modifier order, accessibility. Those are enforced by the build itself —
`EnforceCodeStyleInBuild` and `TreatWarningsAsErrors` are both on, so a naming violation or an
unused using fails the build rather than appearing as a suggestion in an IDE.

Two configuration traps are worth knowing, because both fail silently:

- A `dotnet_naming_rule` severity does **not** enable the rule. Without an explicit
  `dotnet_diagnostic.IDE1006.severity` the entire naming section is ignored at build time.
- `IDE0005` (unused usings) only reports during a build when `GenerateDocumentationFile` is on.

Both are handled in `.editorconfig` and `Directory.Build.props`. If you add style rules, verify them
by building deliberately bad code — the configuration will not tell you it is being ignored.

Run this once so `git blame` skips the reformat commit:

```sh
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

## CI

`.github/workflows/ci.yml` builds, tests, runs the smoke checker against the runner's own `/proc`,
and uploads the tarball.

Running the smoke checker in CI is deliberate rather than redundant. A container's `/proc` differs
from a desktop's in ways that catch assumptions — far fewer processes, no login session, restricted
cgroups, often no GPU — so it exercises the degraded paths that a developer machine never hits.
