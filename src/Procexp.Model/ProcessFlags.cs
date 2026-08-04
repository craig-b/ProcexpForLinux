namespace Procexp.Model;

/// <summary>
/// Bit flags that drive row colouring, badges, and filtering.
/// </summary>
[Flags]
public enum ProcessFlags : uint
{
    None = 0,

    /// <summary>Runs as the current user.</summary>
    OwnProcess = 1 << 0,

    /// <summary>
    /// Managed by systemd as a service — the analog of a Windows service, and of
    /// a launchd daemon on macOS. Determined from the process cgroup rather than
    /// from the parent PID, so it stays correct for services that fork.
    /// </summary>
    Service = 1 << 1,

    /// <summary>Stopped: <c>/proc/PID/stat</c> state <c>T</c> or <c>t</c>.</summary>
    Suspended = 1 << 2,

    /// <summary>
    /// Confined by a mandatory access control policy or running inside a
    /// container — AppArmor/SELinux label, Flatpak, Snap, or a container runtime.
    /// The Linux analog of the macOS App Sandbox.
    /// </summary>
    Sandboxed = 1 << 3,

    /// <summary>Owned by a distribution package rather than installed ad hoc.</summary>
    PackagedBinary = 1 << 4,

    /// <summary>Heuristically packed or obfuscated image.</summary>
    Packed = 1 << 5,

    /// <summary>Appeared since the previous snapshot (green fade).</summary>
    NewProcess = 1 << 6,

    /// <summary>Disappeared since the previous snapshot (red fade).</summary>
    DeadProcess = 1 << 7,

    /// <summary>
    /// Some per-process detail was unavailable. On Linux this is narrower than on
    /// macOS: only <c>io</c>, <c>smaps_rollup</c> and <c>environ</c> are
    /// owner-restricted, so this flag means those specific fields are missing
    /// rather than that the process is opaque.
    /// </summary>
    LimitedInfo = 1 << 8,

    /// <summary>A kernel thread (no user-space address space).</summary>
    KernelThread = 1 << 9,

    /// <summary>Zombie: exited but not yet reaped by its parent.</summary>
    Zombie = 1 << 10,
}
