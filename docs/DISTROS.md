# Distribution compatibility

Nothing here is Arch-specific by design, but it was written on Arch, so this records what has
actually been tested rather than what ought to work.

## Tested

The smoke checker runs the whole data layer against the live kernel and cross-checks it against
native tools, so "passes" below means the parsers agree with `ps`, `ss`, `readelf`, `sha256sum` and
the distribution's own package manager.

| Distribution       | libc  | Packages | Result                                           |
| ------------------ | ----- | -------- | ------------------------------------------------ |
| EndeavourOS / Arch | glibc | pacman   | Passes — full desktop, 651 processes             |
| Debian 13          | glibc | dpkg     | Passes — `coreutils 9.7-3`                       |
| Ubuntu 24.04       | glibc | dpkg     | Passes — `coreutils 9.4-3ubuntu6.2`              |
| Fedora             | glibc | rpm      | Passes — `coreutils 9.10-3.fc44`                 |
| Alpine 3           | musl  | apk      | Passes — `busybox 1.37.0-r31`, with a musl build |

The glibc binary runs unmodified on Arch, Debian, Ubuntu and Fedora. Alpine needs a separate build —
see below.

Testing Alpine found two real bugs, both since fixed: `apk info --who-owns` returns a single
concatenated `name-version-rRELEASE` string rather than separate fields, so the version never
populated; and it phrases its answer differently for a symlink, which the cross-check had not
accounted for.

## musl needs its own build

A glibc-linked binary cannot run on Alpine:

```
Error loading shared library ld-linux-x86-64.so.2: No such file or directory
```

Worse, cross-compiling from a glibc host **appears to work and does not**.
`dotnet publish -r linux-musl-x64` on Arch completes cleanly and produces a binary with musl's ELF
interpreter but glibc's library dependencies, which runs on neither. Native AOT links real object
code, so it needs a linker and sysroot for the target rather than just the .NET runtime pack.

Use `Scripts/build-musl.sh`, which builds inside an Alpine container.

## What varies by distribution

### Package manager — handled

Detected from the presence of the _database_, not the tool, since a container often ships the binary
with nothing in it:

|        | Database probed                           |
| ------ | ----------------------------------------- |
| pacman | `/var/lib/pacman/local`                   |
| dpkg   | `/var/lib/dpkg/status`                    |
| rpm    | `/var/lib/rpm` or `/usr/lib/sysimage/rpm` |
| apk    | `/lib/apk/db/installed`                   |

With none of them the Verified Signer column reads `(unknown)` and everything else works.

`debsums` is not installed by default on Debian, so deep verification returns "could not determine"
rather than claiming the file is unmodified — see `PackageDatabase.IsUnmodified`.

### init system — systemd assumed for one feature

Service classification comes from the cgroup path: `/system.slice/…` or `/init.scope`. On OpenRC,
runit, s6 or sysvinit that never matches, so:

- the **Service** flag never sets, so no pink rows and no unit names
- the **Autostart Location** column falls back to XDG autostart, cron and `/etc/init.d`, which are
  all still indexed

Everything else — the tree, CPU, memory, threads, handles, sockets, provenance — is init-agnostic.
This is a missing feature on those systems, not a failure.

### cgroup hierarchy — handled

v1, v2 and hybrid are all parsed. On hybrid systems the unified line wins, since the per-controller
v1 paths disagree with each other.

### Merged `/usr` — mostly irrelevant

The only hardcoded binary path is in the smoke checker, which now tries `/usr/bin/ls`, `/bin/ls` and
`/usr/bin/coreutils` in turn. The application itself resolves every path from `/proc`.

### User and group lookup

`/etc/passwd` and `/etc/group` are read directly rather than through NSS, to keep a sweep free of
blocking directory lookups. Users defined only in LDAP, SSSD, AD or systemd-homed will not resolve
and show as numeric uids.

### Non-x86

Only `linux-x64` has been built and tested. `linux-arm64` should work — nothing in the parsers is
architecture-specific — with two caveats:

- Native AOT cross-compilation needs a cross toolchain, so build on arm64 hardware or in an arm64
  container.
- The `dirent` offset used for fast directory counting assumes 64-bit `ino_t` and `off_t`, which
  holds for glibc and musl on all 64-bit targets. A 32-bit port would need that checked.

## Containers

The app runs inside a container and reports what the container can see: its own pid namespace, so no
kernel threads and few processes; an overlay root, which the volume list deliberately excludes as a
pseudo-filesystem; and usually no systemd and no GPU.

The smoke checker detects this and skips the assertions the environment cannot satisfy rather than
reporting them as failures, printing what it decided:

```
Environment: container-like, systemd absent, 1 processes
```
