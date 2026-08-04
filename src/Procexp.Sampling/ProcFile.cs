using System.Buffers;
using System.Buffers.Text;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Procexp.Sampling;

/// <summary>
/// Reading and parsing primitives for <c>/proc</c>.
/// </summary>
/// <remarks>
/// Files under <c>/proc</c> report a size of zero and are generated on read, so
/// the usual length-then-read path does not work; everything here reads
/// incrementally into a caller-supplied buffer.
///
/// A full sweep touches several files per process across several hundred
/// processes every second, so these helpers work over raw UTF-8 spans and avoid
/// allocating strings for values that are about to become numbers.
/// </remarks>
internal static class ProcFile
{
    /// <summary>
    /// Read a <c>/proc</c> file into <paramref name="buffer"/>, growing it if
    /// needed. Returns false when the file vanished or is not readable, which is
    /// entirely routine: processes exit mid-sweep, and some files are restricted
    /// to the owning uid.
    /// </summary>
    internal static bool TryRead(string path, ref byte[] buffer, out int length)
    {
        length = 0;

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }
        catch (Exception e)
            when (e
                    is FileNotFoundException
                        or DirectoryNotFoundException
                        or UnauthorizedAccessException
                        or IOException
            )
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

                var space = buffer.Length - total;

                int read;
                try
                {
                    read = RandomAccess.Read(handle, buffer.AsSpan(total, space), total);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Several /proc files enforce permission at read time rather
                    // than at open time — /proc/PID/io is the notable one, and it
                    // surfaces as UnauthorizedAccessException rather than
                    // IOException. Opening it for another user succeeds and only
                    // the read fails, so this catch is load-bearing, not defensive.
                    return false;
                }

                // Read until zero rather than treating a short read as EOF.
                // Multi-record seq_file exports — /proc/PID/maps above all — emit
                // whole records and stop early when the next one will not fit, so
                // a short read routinely arrives with data still pending. Assuming
                // otherwise silently truncates the module list.
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

    /// <summary>Read a symlink target, or null when it cannot be resolved.</summary>
    internal static string? ReadLink(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: false)?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Count entries in a directory without materialising their names.</summary>
    internal static int? TryCountEntries(string path) => NativeMethods.CountDirectoryEntries(path);

    // ---- span parsing -------------------------------------------------------

    /// <summary>
    /// Advance past <paramref name="count"/> space-separated fields, returning
    /// what remains.
    /// </summary>
    internal static ReadOnlySpan<byte> SkipFields(ReadOnlySpan<byte> span, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var space = span.IndexOf((byte)' ');
            if (space < 0)
            {
                return [];
            }

            span = span[(space + 1)..];
        }

        return span;
    }

    /// <summary>Take the next space-separated field and advance the span past it.</summary>
    internal static ReadOnlySpan<byte> NextField(ref ReadOnlySpan<byte> span)
    {
        var space = span.IndexOf((byte)' ');
        if (space < 0)
        {
            var all = span;
            span = [];
            return all;
        }

        var field = span[..space];
        span = span[(space + 1)..];
        return field;
    }

    internal static ulong ParseUInt64(ReadOnlySpan<byte> span) =>
        Utf8Parser.TryParse(span, out ulong value, out _) ? value : 0;

    internal static long ParseInt64(ReadOnlySpan<byte> span) =>
        Utf8Parser.TryParse(span, out long value, out _) ? value : 0;

    internal static int ParseInt32(ReadOnlySpan<byte> span) =>
        Utf8Parser.TryParse(span, out int value, out _) ? value : 0;

    /// <summary>
    /// Find the value of a <c>Key:\tvalue</c> line, as used by
    /// <c>/proc/PID/status</c>, <c>/proc/meminfo</c> and <c>/proc/PID/io</c>.
    /// Returns an empty span when the key is absent.
    /// </summary>
    internal static ReadOnlySpan<byte> FindKeyedValue(
        ReadOnlySpan<byte> content,
        ReadOnlySpan<byte> key
    )
    {
        while (!content.IsEmpty)
        {
            var newline = content.IndexOf((byte)'\n');
            var line = newline < 0 ? content : content[..newline];

            if (line.Length > key.Length && line.StartsWith(key) && line[key.Length] == (byte)':')
            {
                var value = line[(key.Length + 1)..];
                var start = 0;
                while (
                    start < value.Length
                    && (value[start] == (byte)' ' || value[start] == (byte)'\t')
                )
                {
                    start++;
                }

                return value[start..];
            }

            if (newline < 0)
            {
                break;
            }

            content = content[(newline + 1)..];
        }

        return [];
    }

    /// <summary>Parse a <c>NNN kB</c> value from status or meminfo into bytes.</summary>
    internal static ulong ParseKilobytes(ReadOnlySpan<byte> value) => ParseUInt64(value) * 1024;

    internal static string ToString(ReadOnlySpan<byte> span) => Encoding.UTF8.GetString(span);

    /// <summary>Rent a buffer sized for a typical /proc file.</summary>
    internal static byte[] RentBuffer(int size = 4096) => ArrayPool<byte>.Shared.Rent(size);

    internal static void ReturnBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
