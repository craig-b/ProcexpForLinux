# Sysinternals Process Explorer for Linux

A Linux implementation of the core Process Explorer experience, written in C# on
.NET 10 with an Avalonia UI front end. It is a sibling to
[ProcexpForMac](../ProcexpForMac) and follows the same architecture: immutable
snapshots produced by pluggable providers behind narrow interfaces, with the UI
depending only on those interfaces.

This is a **re-implementation, not a source port** — no Swift is shared. What
carries over is the architecture, the data contracts, and the feature set.

## Status

Early. The solution scaffold builds; providers and UI are being filled in.

## Layout

| Project | Role |
|---|---|
| `src/Procexp.Model` | Shared contracts: `ProcessRecord`, `ProcessSnapshot`, provider interfaces, colouring rules. No platform dependencies. |
| `src/Procexp.Sampling` | The unprivileged sampling engine. Parses `/proc` directly. |
| `src/Procexp.SystemStats` | System-wide CPU/memory/disk/network stats and hardware detail. |
| `src/Procexp.Net` | Per-process sockets via netlink `sock_diag`, with a `/proc/net` fallback. |
| `src/Procexp.Provenance` | The Linux analog of code signing — package ownership, build-id, IMA, VirusTotal. |
| `src/Procexp.Autostart` | systemd units, XDG autostart, cron, init.d. |
| `src/Procexp.Actions` | Kill, suspend/resume, renice, restart, sample. |
| `src/Procexp.Gpu` | Per-process GPU usage from DRM `fdinfo` and NVML. |
| `src/Procexp.Privileged` | Client for the privileged helper. |
| `src/Procexp.Helper` | The privileged helper daemon (`procexp-helper`). |
| `src/Procexp.Smoke` | Headless smoke-checker for the data layer (`procexp-smoke`). |
| `src/Procexp.App` | The Avalonia GUI (`procexp`). |

## Requirements

- .NET 10 SDK
- A Linux kernel exposing `/proc` (no `hidepid` restriction for full fidelity)

## Build

```sh
dotnet build ProcexpLinux.slnx
```

Run the headless data-layer check without a GUI:

```sh
dotnet run --project src/Procexp.Smoke
```

## Notes on the port

macOS data sources map onto `/proc` almost one-for-one, and several things get
*easier*: `/proc/PID/cmdline` is world-readable (the macOS version needs a
privileged helper for other users' argv), and per-thread detail comes from
`/proc/PID/task/*/stat` without the task-port dance. See
[docs/PORTING_NOTES.md](docs/PORTING_NOTES.md).

The two areas with no clean equivalent are code signing — replaced by package
provenance, see `Procexp.Provenance` — and the custom table implementation, see
[docs/UI_TABLE_NOTES.md](docs/UI_TABLE_NOTES.md).
