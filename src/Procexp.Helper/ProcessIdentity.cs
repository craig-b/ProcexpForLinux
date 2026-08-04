using System.Buffers.Text;

namespace Procexp.Helper;

/// <summary>
/// Re-verifies process identity inside the helper.
/// </summary>
/// <remarks>
/// The client performs the same check, but a privileged daemon cannot rely on
/// that. If the helper signalled whatever PID it was handed, any client — stale,
/// buggy, or hostile — could kill an arbitrary root process by racing PID reuse:
/// wait for a target PID to be recycled onto something valuable, then replay a
/// request built when it belonged to something disposable.
///
/// Verifying here means the helper only ever acts on the process the caller
/// actually named.
/// </remarks>
internal static class ProcessIdentity
{
    /// <summary>
    /// Confirm the PID still hosts the process with the given start time.
    /// </summary>
    internal static bool Verify(int pid, ulong expectedStartTime)
    {
        if (pid <= 0)
        {
            return false;
        }

        var actual = ReadStartTime(pid);
        if (actual is null)
        {
            return false;
        }

        // A zero expectation is not accepted here, unlike in the unprivileged
        // client. Anything reaching the helper must name a fully-qualified
        // identity, or the guard would be trivially bypassed by omitting it.
        return expectedStartTime != 0 && actual.Value == expectedStartTime;
    }

    internal static ulong? ReadStartTime(int pid)
    {
        byte[] content;
        try
        {
            content = File.ReadAllBytes($"/proc/{pid}/stat");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var span = content.AsSpan();

        // comm is unescaped and may contain spaces and parens, so scan from the
        // right for its closing delimiter.
        var close = span.LastIndexOf((byte)')');
        if (close < 0)
        {
            return null;
        }

        // Start time is the 20th space-separated field after comm.
        ReadOnlySpan<byte> rest = span[(close + 1)..];
        for (var i = 0; i < 19; i++)
        {
            rest = SkipSpaces(rest);
            var end = rest.IndexOf((byte)' ');
            if (end < 0)
            {
                return null;
            }

            rest = rest[end..];
        }

        rest = SkipSpaces(rest);
        return Utf8Parser.TryParse(rest, out ulong value, out _) ? value : null;

        static ReadOnlySpan<byte> SkipSpaces(ReadOnlySpan<byte> span)
        {
            var start = 0;
            while (start < span.Length && span[start] == (byte)' ')
            {
                start++;
            }

            return span[start..];
        }
    }
}
