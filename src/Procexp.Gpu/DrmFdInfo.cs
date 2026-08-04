using System.Buffers.Text;
using Microsoft.Win32.SafeHandles;

namespace Procexp.Gpu;

/// <summary>One DRM client's usage, as reported through a process fdinfo entry.</summary>
internal sealed record DrmClientUsage
{
    public required string Driver { get; init; }

    /// <summary>
    /// Client identity within the device. Several file descriptors in the same
    /// process can refer to one client, so this is what stops engine time being
    /// counted once per descriptor.
    /// </summary>
    public required ulong ClientId { get; init; }

    /// <summary>PCI address, so identical client ids on two GPUs stay distinct.</summary>
    public string Device { get; init; } = "";

    /// <summary>Total nanoseconds busy across every engine.</summary>
    public ulong EngineNanos { get; init; }

    /// <summary>Resident video and system memory attributed to this client.</summary>
    public ulong MemoryBytes { get; init; }
}

/// <summary>
/// Parses the DRM accounting the kernel publishes in <c>/proc/PID/fdinfo</c>.
/// </summary>
/// <remarks>
/// This is the Linux replacement for the Metal-based GPU statistics on macOS, and
/// the only per-process GPU accounting the kernel offers. The format is a
/// documented DRM convention: <c>drm-engine-&lt;name&gt;</c> gives cumulative busy
/// nanoseconds per engine, and the memory keys give resident bytes per region.
///
/// Two things routinely produce wrong numbers here. Engine keys are only emitted
/// once an engine has been used, so their absence means idle rather than
/// unsupported; and a client that opens several descriptors reports identical
/// totals on each, so summing without deduplicating by client id inflates usage
/// by the descriptor count.
/// </remarks>
internal static class DrmFdInfo
{
    /// <summary>
    /// Parse one fdinfo file, returning null when it does not describe a DRM
    /// client.
    /// </summary>
    internal static DrmClientUsage? TryParse(ReadOnlySpan<byte> content)
    {
        string? driver = null;
        string device = "";
        ulong clientId = 0;
        ulong engineNanos = 0;
        ulong memoryBytes = 0;
        var sawClientId = false;

        while (!content.IsEmpty)
        {
            var newline = content.IndexOf((byte)'\n');
            var line = newline < 0 ? content : content[..newline];

            if (line.StartsWith("drm-"u8))
            {
                var colon = line.IndexOf((byte)':');
                if (colon > 0)
                {
                    var key = line[..colon];
                    var value = TrimLeading(line[(colon + 1)..]);

                    if (key.SequenceEqual("drm-driver"u8))
                    {
                        driver = Encoding(value);
                    }
                    else if (key.SequenceEqual("drm-client-id"u8))
                    {
                        clientId = ParseNumber(value);
                        sawClientId = true;
                    }
                    else if (key.SequenceEqual("drm-pdev"u8))
                    {
                        device = Encoding(value);
                    }
                    else if (key.StartsWith("drm-engine-"u8))
                    {
                        // "<n> ns" — sum every engine, since a process using
                        // compute and graphics is busy on both.
                        engineNanos += ParseNumber(value);
                    }
                    else if (key.StartsWith("drm-resident-"u8))
                    {
                        // Resident, not total: total counts memory that has been
                        // evicted and is no longer occupying the device.
                        memoryBytes += ParseSize(value);
                    }
                }
            }

            if (newline < 0)
            {
                break;
            }

            content = content[(newline + 1)..];
        }

        if (driver is null || !sawClientId)
        {
            return null;
        }

        return new DrmClientUsage
        {
            Driver = driver,
            ClientId = clientId,
            Device = device,
            EngineNanos = engineNanos,
            MemoryBytes = memoryBytes,
        };
    }

    private static ReadOnlySpan<byte> TrimLeading(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
        {
            start++;
        }

        return span[start..];
    }

    private static ulong ParseNumber(ReadOnlySpan<byte> span) =>
        Utf8Parser.TryParse(span, out ulong value, out _) ? value : 0;

    /// <summary>Parse a "<c>12 KiB</c>"-style value into bytes.</summary>
    private static ulong ParseSize(ReadOnlySpan<byte> span)
    {
        if (!Utf8Parser.TryParse(span, out ulong value, out var consumed))
        {
            return 0;
        }

        var unit = TrimLeading(span[consumed..]);

        if (unit.StartsWith("KiB"u8))
        {
            return value * 1024;
        }

        if (unit.StartsWith("MiB"u8))
        {
            return value * 1024 * 1024;
        }

        if (unit.StartsWith("GiB"u8))
        {
            return value * 1024 * 1024 * 1024;
        }

        return value;
    }

    private static string Encoding(ReadOnlySpan<byte> span)
    {
        var end = span.Length;
        while (end > 0 && (span[end - 1] == (byte)'\r' || span[end - 1] == (byte)' ' || span[end - 1] == (byte)'\t'))
        {
            end--;
        }

        return System.Text.Encoding.UTF8.GetString(span[..end]);
    }

    /// <summary>Read a small /proc file into a caller-owned buffer.</summary>
    internal static bool TryRead(string path, ref byte[] buffer, out int length)
    {
        length = 0;

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (handle)
        {
            var total = 0;
            while (true)
            {
                if (total == buffer.Length)
                {
                    Array.Resize(ref buffer, buffer.Length * 2);
                }

                int read;
                try
                {
                    read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return false;
                }

                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            length = total;
            return true;
        }
    }
}
