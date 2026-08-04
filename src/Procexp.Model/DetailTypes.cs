namespace Procexp.Model;

/// <summary>Shared shape for the lower-pane column enums.</summary>
public interface ILowerPaneColumn<TSelf>
    where TSelf : struct, Enum
{
    static abstract string Title(TSelf column);
    static abstract double DefaultWidth(TSelf column);
    static abstract bool IsRightAligned(TSelf column);
    static abstract IReadOnlyList<TSelf> DefaultColumns { get; }
    static abstract IReadOnlyList<TSelf> RequiredColumns { get; }
}

// ---------------------------------------------------------------------------
// Threads
// ---------------------------------------------------------------------------

/// <summary>
/// One thread of a process, from <c>/proc/PID/task/TID</c>.
/// </summary>
/// <remarks>
/// Every field here is readable without privilege, which is a straight
/// improvement on macOS: that implementation needs a task port for per-thread
/// detail and falls back to empty stub rows when the kernel refuses.
/// </remarks>
public sealed record ThreadInfo
{
    public required ulong Tid { get; init; }

    /// <summary>Thread name from <c>/proc/PID/task/TID/comm</c>.</summary>
    public string Name { get; init; } = "";

    public double CpuPercent { get; init; }

    /// <summary>User + system time in nanoseconds.</summary>
    public ulong CpuTime { get; init; }

    public ulong UserTime { get; init; }
    public ulong KernelTime { get; init; }

    /// <summary>Human-readable state derived from the raw state character.</summary>
    public string State { get; init; } = "";

    public char StateChar { get; init; }

    /// <summary>Instruction pointer, when readable.</summary>
    public ulong? StartAddress { get; init; }

    /// <summary>Symbol containing <see cref="StartAddress"/>, when it can be resolved.</summary>
    public string? StartSymbol { get; init; }

    /// <summary>
    /// Kernel function the thread is blocked in, from <c>/proc/PID/task/TID/wchan</c>.
    /// This is the genuine Linux counterpart of Process Explorer's Wait Reason
    /// column — macOS exposes nothing equivalent.
    /// </summary>
    public string? WaitChannel { get; init; }

    public int Priority { get; init; }
    public int Nice { get; init; }

    /// <summary>Real-time priority; 0 for ordinary SCHED_OTHER threads.</summary>
    public int RealtimePriority { get; init; }

    public int SchedulingPolicy { get; init; }

    /// <summary>CPU the thread last ran on.</summary>
    public int LastCpu { get; init; }

    public ulong? MinorFaults { get; init; }
    public ulong? MajorFaults { get; init; }
}

public enum ThreadColumn
{
    State,
    Tid,
    Name,
    UserTime,
    KernelTime,
    Cpu,
    CpuTime,
    StartAddress,
    WaitChannel,
    Priority,
    Nice,
    RealtimePriority,
    Policy,
    LastCpu,
}

public static class ThreadColumns
{
    public static string Title(ThreadColumn c) =>
        c switch
        {
            ThreadColumn.State => "State",
            ThreadColumn.Tid => "TID",
            ThreadColumn.Name => "Name",
            ThreadColumn.UserTime => "User Time",
            ThreadColumn.KernelTime => "Kernel Time",
            ThreadColumn.Cpu => "CPU",
            ThreadColumn.CpuTime => "CPU Time",
            ThreadColumn.StartAddress => "Start Address",
            ThreadColumn.WaitChannel => "Wait Reason",
            ThreadColumn.Priority => "Priority",
            ThreadColumn.Nice => "Nice",
            ThreadColumn.RealtimePriority => "RT Pri",
            ThreadColumn.Policy => "Policy",
            ThreadColumn.LastCpu => "CPU #",
            _ => "",
        };

    public static double DefaultWidth(ThreadColumn c) =>
        c switch
        {
            ThreadColumn.State => 110,
            ThreadColumn.Tid => 92,
            ThreadColumn.Name => 150,
            ThreadColumn.UserTime or ThreadColumn.KernelTime or ThreadColumn.CpuTime => 100,
            ThreadColumn.Cpu => 70,
            ThreadColumn.StartAddress => 130,
            ThreadColumn.WaitChannel => 160,
            ThreadColumn.Priority or ThreadColumn.Nice or ThreadColumn.RealtimePriority => 76,
            ThreadColumn.Policy => 94,
            ThreadColumn.LastCpu => 62,
            _ => 90,
        };

    public static bool IsRightAligned(ThreadColumn c) =>
        c
            is ThreadColumn.Tid
                or ThreadColumn.UserTime
                or ThreadColumn.KernelTime
                or ThreadColumn.Cpu
                or ThreadColumn.CpuTime
                or ThreadColumn.Priority
                or ThreadColumn.Nice
                or ThreadColumn.RealtimePriority
                or ThreadColumn.LastCpu;

    /// <summary>
    /// Mirrors Process Explorer's thread-view defaults. Wait Reason is included
    /// where the macOS build had to omit it.
    /// </summary>
    public static readonly IReadOnlyList<ThreadColumn> Default =
    [
        ThreadColumn.State,
        ThreadColumn.Tid,
        ThreadColumn.UserTime,
        ThreadColumn.KernelTime,
        ThreadColumn.Cpu,
        ThreadColumn.CpuTime,
        ThreadColumn.StartAddress,
        ThreadColumn.WaitChannel,
        ThreadColumn.Priority,
    ];

    public static readonly IReadOnlyList<ThreadColumn> Required = [ThreadColumn.Tid];
}

// ---------------------------------------------------------------------------
// Modules / mapped images
// ---------------------------------------------------------------------------

/// <summary>
/// One file-backed mapping in a process address space, from
/// <c>/proc/PID/maps</c>. The Linux equivalent of a loaded DLL.
/// </summary>
public sealed record ModuleInfo
{
    /// <summary>Path, unique within a process.</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    /// <summary>Lowest mapped address across all segments of this file.</summary>
    public ulong LoadAddress { get; init; }

    /// <summary>Total bytes mapped from this file.</summary>
    public ulong Size { get; init; }

    /// <summary>Union of the segment permissions, e.g. <c>r-xp</c>.</summary>
    public string Permissions { get; init; } = "";

    public ProvenanceInfo? Provenance { get; init; }

    public string? Description { get; init; }
    public string? Company { get; init; }
    public string? Version { get; init; }

    /// <summary>False for the main executable, true for shared libraries and data.</summary>
    public bool IsSharedLibrary { get; init; }
}

public enum ModuleColumn
{
    Name,
    Description,
    Company,
    Version,
    Path,
    Provenance,
    Base,
    Size,
    Permissions,
}

public static class ModuleColumns
{
    public static string Title(ModuleColumn c) =>
        c switch
        {
            ModuleColumn.Name => "Name",
            ModuleColumn.Description => "Description",
            ModuleColumn.Company => "Company",
            ModuleColumn.Version => "Version",
            ModuleColumn.Path => "Path",
            ModuleColumn.Provenance => "Package",
            ModuleColumn.Base => "Base",
            ModuleColumn.Size => "Size",
            ModuleColumn.Permissions => "Perms",
            _ => "",
        };

    public static double DefaultWidth(ModuleColumn c) =>
        c switch
        {
            ModuleColumn.Name => 200,
            ModuleColumn.Description => 200,
            ModuleColumn.Company => 150,
            ModuleColumn.Version => 90,
            ModuleColumn.Path => 320,
            ModuleColumn.Provenance => 180,
            ModuleColumn.Base => 130,
            ModuleColumn.Size => 80,
            ModuleColumn.Permissions => 62,
            _ => 100,
        };

    public static bool IsRightAligned(ModuleColumn c) =>
        c is ModuleColumn.Base or ModuleColumn.Size;

    public static readonly IReadOnlyList<ModuleColumn> Default =
    [
        ModuleColumn.Name,
        ModuleColumn.Description,
        ModuleColumn.Company,
        ModuleColumn.Path,
    ];

    public static readonly IReadOnlyList<ModuleColumn> Required = [ModuleColumn.Name];
}

// ---------------------------------------------------------------------------
// File descriptors / handles
// ---------------------------------------------------------------------------

/// <summary>What a file descriptor refers to.</summary>
public enum FdKind
{
    Unknown,
    File,
    Directory,
    Socket,
    Pipe,
    CharacterDevice,
    BlockDevice,
    SymbolicLink,
    EventFd,
    EventPoll,
    TimerFd,
    SignalFd,
    Inotify,
    Fanotify,
    MemFd,
    PidFd,
    UserFaultFd,
    Anonymous,
}

/// <summary>
/// One open file descriptor, from <c>/proc/PID/fd</c> and
/// <c>/proc/PID/fdinfo</c>. The Handles-pane row type.
/// </summary>
public sealed record FileDescriptorInfo
{
    public required int Fd { get; init; }
    public required FdKind Kind { get; init; }

    /// <summary>Resolved target: a path, an <c>addr:port</c> pair, or a description.</summary>
    public required string Name { get; init; }

    /// <summary>Raw <c>flags</c> from fdinfo — the open mode and status bits.</summary>
    public uint? OpenFlags { get; init; }

    /// <summary>Decoded access mode, e.g. <c>r</c>, <c>rw</c>, <c>w (append)</c>.</summary>
    public string? Access { get; init; }

    public long? Offset { get; init; }
    public long? Size { get; init; }
    public ulong? Inode { get; init; }

    /// <summary>Device the file lives on, as major:minor.</summary>
    public string? Device { get; init; }

    public SocketInfo? Socket { get; init; }
}

public enum HandleColumn
{
    Kind,
    Name,
    Fd,
    Access,
    Offset,
    Size,
    Inode,
    Device,
    SocketFamily,
    SocketProtocol,
    SocketState,
    SocketQueues,
}

public static class HandleColumns
{
    public static string Title(HandleColumn c) =>
        c switch
        {
            HandleColumn.Kind => "Type",
            HandleColumn.Name => "Name",
            HandleColumn.Fd => "FD",
            HandleColumn.Access => "Access",
            HandleColumn.Offset => "Offset",
            HandleColumn.Size => "Size",
            HandleColumn.Inode => "Inode",
            HandleColumn.Device => "Device",
            HandleColumn.SocketFamily => "Family",
            HandleColumn.SocketProtocol => "Protocol",
            HandleColumn.SocketState => "Socket State",
            HandleColumn.SocketQueues => "Queues",
            _ => "",
        };

    public static double DefaultWidth(HandleColumn c) =>
        c switch
        {
            HandleColumn.Kind => 100,
            HandleColumn.Name => 440,
            HandleColumn.Fd => 56,
            HandleColumn.Access => 76,
            HandleColumn.Offset or HandleColumn.Size => 86,
            HandleColumn.Inode => 110,
            HandleColumn.Device => 80,
            HandleColumn.SocketFamily or HandleColumn.SocketProtocol => 86,
            HandleColumn.SocketState => 110,
            HandleColumn.SocketQueues => 90,
            _ => 90,
        };

    public static bool IsRightAligned(HandleColumn c) =>
        c
            is HandleColumn.Fd
                or HandleColumn.Offset
                or HandleColumn.Size
                or HandleColumn.Inode
                or HandleColumn.SocketQueues;

    public static readonly IReadOnlyList<HandleColumn> Default =
    [
        HandleColumn.Kind,
        HandleColumn.Name,
        HandleColumn.Fd,
    ];

    public static readonly IReadOnlyList<HandleColumn> Required =
    [
        HandleColumn.Kind,
        HandleColumn.Name,
        HandleColumn.Fd,
    ];
}

// ---------------------------------------------------------------------------
// Sockets
// ---------------------------------------------------------------------------

public enum SocketProtocol
{
    Unknown,
    Tcp,
    Tcp6,
    Udp,
    Udp6,
    Unix,
    Netlink,
    Raw,
    Packet,
}

/// <summary>
/// One socket owned by a process. Sourced from netlink <c>sock_diag</c>, joined
/// to the owning process through the socket inode found in <c>/proc/PID/fd</c>.
/// </summary>
public sealed record SocketInfo
{
    /// <summary>The descriptor in the owning process.</summary>
    public required int Fd { get; init; }

    public required SocketProtocol Protocol { get; init; }

    public string LocalAddress { get; init; } = "";
    public ushort LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public ushort RemotePort { get; init; }

    /// <summary>Decoded TCP state, e.g. <c>ESTABLISHED</c>; empty for stateless protocols.</summary>
    public string State { get; init; } = "";

    public byte? TcpStateRaw { get; init; }
    public ulong Inode { get; init; }
    public uint? Uid { get; init; }

    public uint? ReceiveQueue { get; init; }
    public uint? SendQueue { get; init; }

    /// <summary>Filesystem path for AF_UNIX sockets, when bound.</summary>
    public string? UnixPath { get; init; }

    /// <summary>Reverse-DNS name for the remote address, resolved asynchronously.</summary>
    public string? RemoteHostName { get; init; }
}
