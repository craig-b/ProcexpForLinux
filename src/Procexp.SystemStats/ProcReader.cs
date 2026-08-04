using System.Buffers;
using System.Buffers.Text;
using Microsoft.Win32.SafeHandles;

namespace Procexp.Metrics;

/// <summary>
/// Minimal <c>/proc</c> reading and span parsing for the system-wide files.
/// </summary>
/// <remarks>
/// Deliberately duplicated from the equivalent helper in Procexp.Sampling rather
/// than shared. The two have different lifetimes and different callers — this one
/// reads a handful of fixed files once a second, that one reads thousands of
/// per-process files — and coupling them would mean the sampling engine's hot
/// path could not change without regression-testing this.
/// </remarks>
internal static class ProcReader
{
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

    internal static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Iterate the lines of a buffer without allocating strings.</summary>
    internal static LineEnumerator Lines(ReadOnlySpan<byte> content) => new(content);

    internal ref struct LineEnumerator(ReadOnlySpan<byte> content)
    {
        private ReadOnlySpan<byte> _remaining = content;

        public ReadOnlySpan<byte> Current { get; private set; } = default;

        public readonly LineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_remaining.IsEmpty)
            {
                return false;
            }

            var newline = _remaining.IndexOf((byte)'\n');
            if (newline < 0)
            {
                Current = _remaining;
                _remaining = default;
            }
            else
            {
                Current = _remaining[..newline];
                _remaining = _remaining[(newline + 1)..];
            }

            return true;
        }
    }

    /// <summary>Take the next whitespace-separated field, advancing the span.</summary>
    internal static ReadOnlySpan<byte> NextField(ref ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && (span[start] == (byte)' ' || span[start] == (byte)'\t'))
        {
            start++;
        }

        span = span[start..];
        if (span.IsEmpty)
        {
            return [];
        }

        var end = 0;
        while (end < span.Length && span[end] != (byte)' ' && span[end] != (byte)'\t')
        {
            end++;
        }

        var field = span[..end];
        span = span[end..];
        return field;
    }

    internal static ulong ParseUInt64(ReadOnlySpan<byte> span) =>
        Utf8Parser.TryParse(span, out ulong value, out _) ? value : 0;

    internal static byte[] Rent(int size = 8192) => ArrayPool<byte>.Shared.Rent(size);

    internal static void Return(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);
}
