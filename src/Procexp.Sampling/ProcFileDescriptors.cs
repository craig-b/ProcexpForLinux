using System.Globalization;
using Procexp.Model;

namespace Procexp.Sampling;

/// <summary>
/// Open file descriptors, from <c>/proc/PID/fd</c> and <c>/proc/PID/fdinfo</c> —
/// the Handles pane's row source.
/// </summary>
/// <remarks>
/// Richer than the macOS equivalent: every descriptor names its target through a
/// symlink, and fdinfo adds the file position, open flags, and per-type detail
/// that <c>proc_pidfdinfo</c> does not expose.
/// </remarks>
internal static class ProcFileDescriptors
{
    internal static IReadOnlyList<FileDescriptorInfo> Read(ProcessId id)
    {
        var directory = $"/proc/{id.Pid}/fd";

        string[] entries;
        try
        {
            entries = Directory.GetFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw ProviderException.NotPermitted($"{directory} is not readable");
        }

        var result = new List<FileDescriptorInfo>(entries.Length);
        var buffer = ProcFile.RentBuffer();

        try
        {
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (!int.TryParse(name, out var fd))
                {
                    continue;
                }

                // Descriptors close constantly; a vanished one is not an error.
                var target = ProcFile.ReadLink(entry) ?? ReadRawLink(entry);
                if (target is null)
                {
                    continue;
                }

                var (kind, display) = Classify(target);
                var info = ReadFdInfo($"/proc/{id.Pid}/fdinfo/{name}", ref buffer);

                result.Add(
                    new FileDescriptorInfo
                    {
                        Fd = fd,
                        Kind = kind,
                        Name = display,
                        Offset = info.Position,
                        OpenFlags = info.Flags,
                        Access = DecodeAccess(info.Flags),
                        Inode = ExtractInode(target) ?? info.Inode,
                    }
                );
            }
        }
        finally
        {
            ProcFile.ReturnBuffer(buffer);
        }

        result.Sort(static (a, b) => a.Fd.CompareTo(b.Fd));
        return result;
    }

    /// <summary>
    /// Many descriptor targets are not real paths — <c>socket:[12345]</c>,
    /// <c>anon_inode:[eventfd]</c> — so File.ResolveLinkTarget can reject them.
    /// Fall back to reading the link text as given.
    /// </summary>
    private static string? ReadRawLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (FdKind Kind, string Display) Classify(string target)
    {
        if (target.StartsWith("socket:", StringComparison.Ordinal))
        {
            return (FdKind.Socket, target);
        }

        if (target.StartsWith("pipe:", StringComparison.Ordinal))
        {
            return (FdKind.Pipe, target);
        }

        if (target.StartsWith("anon_inode:", StringComparison.Ordinal))
        {
            var inner = target["anon_inode:".Length..].Trim('[', ']');
            var kind = inner switch
            {
                "eventfd" => FdKind.EventFd,
                "eventpoll" or "[eventpoll]" => FdKind.EventPoll,
                "timerfd" => FdKind.TimerFd,
                "signalfd" => FdKind.SignalFd,
                "inotify" => FdKind.Inotify,
                "fanotify" => FdKind.Fanotify,
                "pidfd" => FdKind.PidFd,
                "userfaultfd" => FdKind.UserFaultFd,
                _ => FdKind.Anonymous,
            };
            return (kind, target);
        }

        if (target.StartsWith("/memfd:", StringComparison.Ordinal))
        {
            return (FdKind.MemFd, target);
        }

        try
        {
            if (Directory.Exists(target))
            {
                return (FdKind.Directory, target);
            }

            var info = new FileInfo(target);
            if (info.Exists)
            {
                var mode = info.UnixFileMode;
                // There is no direct "is a device" test, so infer from the path,
                // which is reliable for the /dev tree in practice.
                if (target.StartsWith("/dev/", StringComparison.Ordinal))
                {
                    return (FdKind.CharacterDevice, target);
                }

                _ = mode;
                return (FdKind.File, target);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through to Unknown.
        }

        return (FdKind.Unknown, target);
    }

    private static ulong? ExtractInode(string target)
    {
        var open = target.IndexOf('[');
        var close = target.IndexOf(']');
        if (open < 0 || close < open)
        {
            return null;
        }

        return ulong.TryParse(
            target.AsSpan(open + 1, close - open - 1),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var inode
        )
            ? inode
            : null;
    }

    private readonly record struct FdInfo(long? Position, uint? Flags, ulong? Inode);

    private static FdInfo ReadFdInfo(string path, ref byte[] buffer)
    {
        if (!ProcFile.TryRead(path, ref buffer, out var length))
        {
            return default;
        }

        var content = buffer.AsSpan(0, length);

        long? position = null;
        var pos = ProcFile.FindKeyedValue(content, "pos"u8);
        if (!pos.IsEmpty)
        {
            position = ProcFile.ParseInt64(pos);
        }

        uint? flags = null;
        var flagsValue = ProcFile.FindKeyedValue(content, "flags"u8);
        if (!flagsValue.IsEmpty)
        {
            // fdinfo reports flags in octal.
            flags = ParseOctal(flagsValue);
        }

        ulong? inode = null;
        var ino = ProcFile.FindKeyedValue(content, "ino"u8);
        if (!ino.IsEmpty)
        {
            inode = ProcFile.ParseUInt64(ino);
        }

        return new FdInfo(position, flags, inode);
    }

    private static uint ParseOctal(ReadOnlySpan<byte> span)
    {
        uint value = 0;
        foreach (var b in span)
        {
            if (b is < (byte)'0' or > (byte)'7')
            {
                break;
            }

            value = (value * 8) + (uint)(b - (byte)'0');
        }

        return value;
    }

    private const uint OAccMode = 0x3;
    private const uint OWrOnly = 0x1;
    private const uint ORdWr = 0x2;
    private const uint OAppend = 0x400;
    private const uint ONonBlock = 0x800;
    private const uint OCloExec = 0x80000;

    /// <summary>
    /// Decode the open mode into the Access column, mirroring how the macOS build
    /// renders it: the names, then the raw value in parentheses.
    /// </summary>
    internal static string? DecodeAccess(uint? flags)
    {
        if (flags is not { } f)
        {
            return null;
        }

        var parts = new List<string>(4)
        {
            (f & OAccMode) switch
            {
                OWrOnly => "Write",
                ORdWr => "Read/Write",
                _ => "Read",
            },
        };

        if ((f & OAppend) != 0)
        {
            parts.Add("Append");
        }

        if ((f & ONonBlock) != 0)
        {
            parts.Add("Non-blocking");
        }

        if ((f & OCloExec) != 0)
        {
            parts.Add("Close-on-exec");
        }

        return $"{string.Join(", ", parts)} (0o{Convert.ToString(f, 8)})";
    }
}
