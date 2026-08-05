# The privileged helper

## What it is for

Almost everything Process Explorer shows on Linux is world-readable, so the app runs unprivileged
and the helper is optional. It exists for these:

| Capability                        | Why it needs privilege                                                                               |
| --------------------------------- | ---------------------------------------------------------------------------------------------------- |
| `/proc/PID/io`                    | Mode 0400, owned by the process's user. Fills the I/O Read/Write columns for other users' processes. |
| `/proc/PID/smaps_rollup`          | Same restriction. Supplies proportional set size, the Private Bytes column.                          |
| `/proc/PID/environ`               | Same restriction. Supplies the Environment tab.                                                      |
| `/proc/PID/maps`                  | Gated by `ptrace_may_access`. Supplies the lower pane's mapped-files view.                           |
| `/proc/PID/fd`, `fdinfo`          | Same gate. Supplies the handles view.                                                                |
| Signalling other users' processes | Kill, suspend, resume and renice across user boundaries.                                             |

Without the helper, those fields are blank and cross-user actions fail — nothing else changes.

This is a smaller job than the macOS equivalent, where the privileged helper also supplies
per-thread detail and command lines for any process outside the user's own session, because
`task_for_pid` is restricted. On Linux `/proc/PID/cmdline` is world-readable and the whole of
`/proc/PID/task` — thread list, per-thread CPU, wait channel — needs no privilege at all. Mapped
files and descriptors do, matching macOS.

## Trust model

**Authorisation is by user identity, not by binary identity.**

The macOS helper validates the _code signature_ of the connecting client, so only the genuine,
correctly-signed Process Explorer can talk to it. That model does not transfer, and imitating it
would be security theatre: on Linux any binary a user runs carries that user's full authority.
Checking which executable connected buys nothing, because anyone who can run code as an authorised
user can equally run the real client — or simply read the socket protocol, which is a few lines of
JSON.

So the gate is:

1. **Filesystem permissions on the socket** — the real control. `/run/procexp/helper.sock` is mode
   `0660`, owned `root:procexp`. The kernel refuses everyone else before a single byte is exchanged.
2. **`SO_PEERCRED`** — a second check inside the daemon, and the source of audit log identity. These
   credentials are filled in by the kernel at connect time and cannot be forged by the peer.
3. **Process identity re-verification** — every operation that names a process re-reads its start
   time from `/proc/PID/stat` and compares it against the identity the client supplied.

Point 3 is not redundant with the client's own check. A privileged daemon that signalled whatever
PID it was handed would let a stale or hostile client kill an arbitrary root process by racing PID
reuse: wait for a PID to be recycled onto something valuable, then replay a request built when it
belonged to something disposable. Verifying in the helper means it only ever acts on the process the
caller actually named.

## What membership of the `procexp` group grants

**Treat it as equivalent to sudo access.** A member can:

- read the environment of _any_ process on the machine, which routinely contains API tokens,
  database passwords and session secrets;
- read I/O and memory statistics for any process;
- kill, stop, continue and renice any process except pid 1.

This is inherent to what a process explorer does — the Windows original shows the same things when
run elevated — but it means adding a user to this group is a privilege grant, not a convenience.

If that is more than you want, do not install the helper. The app is fully functional without it.

## What the helper deliberately cannot do

- There is **no "read arbitrary path" operation**. The client chooses an operation from a closed set
  and supplies a process identity; the helper decides which path that maps to. A client cannot ask
  for `/etc/shadow`.
- **Signals are restricted** to HUP, INT, KILL, TERM, CONT and STOP. It is not a general
  signal-injection primitive.
- **pid 1 is refused** outright.
- **Environment reads are capped** at 512 KiB, so a process with a huge environment cannot be used
  to exhaust the daemon's memory.
- **Detail reads return parsed rows, not file contents.** The maps and descriptor operations hand
  back structured records, so the helper never becomes a way to read arbitrary bytes out of a file
  the caller could not open.
- The systemd unit drops every capability except `CAP_DAC_READ_SEARCH`, `CAP_KILL`, `CAP_SYS_NICE`
  and `CAP_CHOWN`, sets `NoNewPrivileges`, restricts address families to `AF_UNIX`, denies all IP
  addressing, and applies the `@system-service` syscall filter. A flaw in the daemon cannot load a
  module, mount a filesystem or reach the network.

## Install

`sudo ./Scripts/install.sh --enable-helper` performs all of the below; what follows is the manual
equivalent.

```sh
sudo groupadd -f procexp
sudo usermod -aG procexp "$USER" # log out and back in for this to take effect

sudo install -Dm755 procexp-helper /usr/lib/procexp/procexp-helper
sudo install -Dm644 packaging/procexp-helper.service /etc/systemd/system/procexp-helper.service

sudo systemctl daemon-reload
sudo systemctl enable --now procexp-helper
```

Verify:

```sh
systemctl status procexp-helper
ls -l /run/procexp/helper.sock # expect srw-rw---- root procexp
```

## Uninstall

```sh
sudo systemctl disable --now procexp-helper
sudo rm /etc/systemd/system/procexp-helper.service /usr/lib/procexp/procexp-helper
sudo systemctl daemon-reload
sudo groupdel procexp
```

## Auditing

Every environment read, signal and renice is logged with the requesting uid and pid:

```sh
journalctl -u procexp-helper
```

```
uid 1000 (pid 4821) read the environment of pid 1
uid 1000 (pid 4821) sent signal 15 to pid 90210
```
