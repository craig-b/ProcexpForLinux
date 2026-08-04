# Porting notes: macOS data sources → Linux

The macOS implementation reads process state through `libproc`, `sysctl`, Mach
host statistics and IOKit. Nearly all of it maps onto `/proc` and `/sys`. This
table is the working reference for the sampling engine.

## Per-process

| `ProcessRecord` field | macOS source | Linux source |
|---|---|---|
| `pid`, `ppid` | `proc_bsdinfo` | `/proc/PID/stat` fields 1, 4 |
| `startTime` (identity) | `pbi_start_tvsec` | `/proc/PID/stat` field 22 (ticks since boot) + `btime` from `/proc/stat` |
| `name` | `proc_name` / `pbi_comm` | `/proc/PID/comm`, then basename of `/proc/PID/exe` |
| `executablePath` | `proc_pidpath` | `readlink /proc/PID/exe` |
| `commandLine` | `proc_args` — **EPERM for other users** | `/proc/PID/cmdline` — **world-readable** |
| `environment` | `proc_args` — EPERM for other users | `/proc/PID/environ` — owner or `CAP_SYS_PTRACE` |
| `uid`, `userName` | `pbi_uid` | `/proc/PID/status` `Uid:`, resolved via `/etc/passwd` |
| `cpuTime` | `proc_taskinfo.pti_total_user/system` | `/proc/PID/stat` `utime`+`stime`, ticks → ns via `sysconf(_SC_CLK_TCK)` |
| `threadCount` | `pti_threadnum` | `/proc/PID/status` `Threads:` |
| `residentSize` | `pti_resident_size` | `/proc/PID/status` `VmRSS` |
| `virtualSize` | `pti_virtual_size` | `/proc/PID/status` `VmSize` |
| `physFootprint` | `proc_pid_rusage` | `/proc/PID/smaps_rollup` `Pss` — owner-restricted |
| `pageFaults` | `pti_faults` | `/proc/PID/stat` `min_flt` + `maj_flt` |
| `diskBytesRead/Written` | `proc_pid_rusage` | `/proc/PID/io` — **owner-restricted, needs helper** |
| `fileDescriptorCount` | `proc_pidinfo(PROC_PIDLISTFDS)` | count of `/proc/PID/fd` entries |
| `nice`, `priority` | `pbi_nice`, `pti_priority` | `/proc/PID/stat` fields 18, 19 |
| `sessionTTY` | `e_tdev` | `/proc/PID/stat` field 7 (`tty_nr`), decoded to `/dev/pts/N` |
| `is64Bit` | `PROC_FLAG_LP64` | ELF class in `/proc/PID/exe` header |

## Process flags

| Flag | macOS | Linux |
|---|---|---|
| `service` | ppid==1 && uid<500 (launchd daemon) | `/proc/PID/cgroup` → systemd `*.service` unit |
| `suspended` | task suspend count | `/proc/PID/stat` state `T` |
| `sandboxed` | App Sandbox entitlement | `/proc/PID/attr/current` (AppArmor/SELinux), `.flatpak-info`, snap cgroup |
| `ownProcess` | `uid == getuid()` | same |
| `limitedTaskInfo` | no task port | unreadable `/proc/PID/{io,smaps_rollup}` |

## Detail views

| View | macOS | Linux |
|---|---|---|
| Modules / images | `proc_regionwithpathinfo` walk | `/proc/PID/maps` — **owner only** |
| Handles / fds | `proc_pidfdinfo` | `/proc/PID/fd/*` + `fdinfo/*` — **owner only** |
| Threads | `task_threads` — **needs task port** | `/proc/PID/task/*/stat`, `comm`, `wchan` — **unprivileged** |
| Sockets | `proc_pidfdinfo(SOCKETINFO)` | netlink `sock_diag`, joined to fd socket inodes |

### The `ptrace_may_access` gate

Most of `/proc` is world-readable, but not all of it. `maps`, `fd`, `fdinfo`,
`environ`, `io`, `smaps_rollup`, `stack` and `syscall` are gated by
`ptrace_may_access`, so they are readable only by the process owner (or with
`CAP_SYS_PTRACE`). Measured on a running system:

| Path | Own process | Another user's |
|---|---|---|
| `stat`, `status`, `cmdline`, `cgroup`, `comm` | yes | **yes** |
| `task/*/stat`, `task/*/wchan` | yes | **yes** |
| `maps`, `fd`, `fdinfo` | yes | **no** |
| `io`, `smaps_rollup`, `environ` | yes | **no** |

The threads win is real and survives this: the whole thread list, including
per-thread CPU and wait channel, reads cross-user. Modules and handles do not,
which is the same restriction macOS applies — so the lower pane must report
"not permitted" rather than an empty list, and the helper's remit covers `maps`
and `fd` as well as the three files originally identified.

## System-wide

| Stat | macOS | Linux |
|---|---|---|
| Total + per-core CPU | `host_processor_info` | `/proc/stat` `cpu` / `cpuN` lines, delta'd |
| Memory | `host_statistics64` (HOST_VM_INFO64) | `/proc/meminfo` |
| Compressed memory | `vm_stat` compressor pages | zram `/sys/block/zram0/mm_stat`, zswap debugfs |
| Swap | `sysctl VM_SWAPUSAGE` | `/proc/meminfo` `SwapTotal`/`SwapFree` |
| Disk I/O | IOKit `IOBlockStorageDriver` | `/proc/diskstats`, delta'd |
| Network I/O | `sysctl NET_RT_IFLIST2` | `/proc/net/dev`, delta'd |
| Handle count | sum of per-process fds | `/proc/sys/fs/file-nr` |
| GPU | Metal | DRM `fdinfo` (`drm-engine-*`), NVML for NVIDIA |

## Things with no direct equivalent

**Code signing.** `Security.framework` has no Linux counterpart; ELF userspace
binaries are essentially never signed. Replaced by package provenance — see
`Procexp.Provenance`. SHA-256 and the VirusTotal lookup carry over unchanged.

**Bundle metadata.** `Info.plist` supplies `version`, `companyName` and
`displayDescription` on macOS. On Linux these come from the owning package
(`pacman -Qo`, `dpkg -S`, `rpm -qf`) and from `.desktop` files.

**launchd.** Autostart resolution moves to systemd units, XDG autostart, and
cron.

## What gets easier

- `/proc/PID/cmdline` is world-readable, so the privileged helper is no longer
  needed for other users' command lines.
- Per-thread detail needs no task port, so the degraded stub-thread path in the
  macOS provider has no Linux equivalent — real thread data is always available.
- `/proc/PID/maps` is a cleaner module list than walking VM regions.

## What still needs privilege

Everything behind the `ptrace_may_access` gate above: `io`, `smaps_rollup`,
`environ`, `maps`, `fd` and `fdinfo`. That is the remaining justification for the
helper daemon — still far less than the XPC subsystem the macOS version needs,
since the process list, the tree, command lines and the entire thread view all
work unprivileged.
