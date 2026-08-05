using System.Text.Json.Serialization;

namespace Procexp.Privileged;

/// <summary>Shared constants describing where the helper lives.</summary>
public static class HelperConstants
{
    /// <summary>
    /// Socket path. Under <c>/run</c> so it is tmpfs-backed and disappears on
    /// reboot rather than lingering as a stale path.
    /// </summary>
    public const string SocketPath = "/run/procexp/helper.sock";

    public const string SocketDirectory = "/run/procexp";

    /// <summary>
    /// Group permitted to talk to the helper. Membership is equivalent to being
    /// able to read any process's I/O counters, memory detail and environment,
    /// and to signal any process — so it should be treated like sudo access.
    /// </summary>
    public const string AccessGroup = "procexp";

    public const string SystemdUnit = "procexp-helper.service";

    /// <summary>Wire protocol version, so a stale helper is detected rather than misread.</summary>
    public const int ProtocolVersion = 1;
}

/// <summary>What the client is asking the helper to do.</summary>
public enum HelperOperation
{
    /// <summary>Version and capability handshake.</summary>
    Hello,

    /// <summary>Read <c>/proc/PID/io</c>.</summary>
    ReadIo,

    /// <summary>Read <c>/proc/PID/smaps_rollup</c> for proportional set size.</summary>
    ReadProportionalMemory,

    /// <summary>Read <c>/proc/PID/environ</c>.</summary>
    ReadEnvironment,

    /// <summary>
    /// Enumerate mapped files from <c>/proc/PID/maps</c>.
    /// </summary>
    /// <remarks>
    /// The helper parses and returns structured rows rather than raw file text.
    /// Parsing on the privileged side keeps one implementation of a fiddly format
    /// instead of two, and means the client never has to trust itself to parse
    /// something it could not read.
    /// </remarks>
    ReadModules,

    /// <summary>Enumerate descriptors from <c>/proc/PID/fd</c> and <c>fdinfo</c>.</summary>
    ReadFileDescriptors,

    /// <summary>Send a signal.</summary>
    Signal,

    /// <summary>Change scheduling priority.</summary>
    SetNice,

    /// <summary>
    /// Read <c>/proc/PID/task/TID/stack</c> — a thread's kernel stack.
    /// </summary>
    /// <remarks>
    /// Helper-only by nature rather than by policy: the kernel gates this file
    /// behind CAP_SYS_ADMIN, so not even the owning user can read it directly.
    /// </remarks>
    ReadThreadKernelStack,

    /// <summary>Pin another user's process to a set of CPUs.</summary>
    SetAffinity,
}

/// <summary>One request. Serialised as a single line of JSON.</summary>
public sealed record HelperRequest
{
    [JsonPropertyName("op")]
    public required HelperOperation Operation { get; init; }

    [JsonPropertyName("pid")]
    public int Pid { get; init; }

    /// <summary>
    /// Start time from <c>/proc/PID/stat</c>, forming the process identity.
    /// </summary>
    /// <remarks>
    /// The helper re-verifies this itself rather than trusting the client to have
    /// done so. A privileged daemon that signals whatever PID it is handed would
    /// let a stale — or malicious — client kill an arbitrary process by racing PID
    /// reuse.
    /// </remarks>
    [JsonPropertyName("start")]
    public ulong StartTime { get; init; }

    [JsonPropertyName("signal")]
    public int Signal { get; init; }

    [JsonPropertyName("nice")]
    public int Nice { get; init; }

    /// <summary>Thread id, for the per-thread operations.</summary>
    [JsonPropertyName("tid")]
    public int Tid { get; init; }

    /// <summary>CPU affinity mask as hex-encoded cpu_set_t bytes.</summary>
    [JsonPropertyName("mask")]
    public string? Mask { get; init; }
}

/// <summary>One response.</summary>
public sealed record HelperResponse
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Raw file contents for the read operations.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("version")]
    public int Version { get; init; }

    public static HelperResponse Failure(string error) => new() { Ok = false, Error = error };

    public static HelperResponse Success(string? content = null) =>
        new() { Ok = true, Content = content };
}

[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperResponse))]
[JsonSerializable(typeof(IReadOnlyList<Model.ModuleInfo>))]
[JsonSerializable(typeof(IReadOnlyList<Model.FileDescriptorInfo>))]
public sealed partial class HelperJsonContext : JsonSerializerContext;
