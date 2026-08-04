using System.Runtime.InteropServices;

namespace Procexp.Sampling;

internal static partial class NativeMethods
{
    /// <summary>Clock ticks per second — the unit of the times in /proc/PID/stat.</summary>
    private const int ScClkTck = 2;

    [LibraryImport("libc", EntryPoint = "sysconf", SetLastError = true)]
    private static partial long SysConf(int name);

    [LibraryImport("libc", EntryPoint = "getuid")]
    internal static partial uint GetUid();

    /// <summary>
    /// Ticks per second. Effectively always 100 on Linux, but the kernel is free
    /// to be built otherwise, and getting this wrong silently scales every CPU
    /// number in the app.
    /// </summary>
    internal static long ClockTicksPerSecond { get; } = ResolveClockTicks();

    private static long ResolveClockTicks()
    {
        var value = SysConf(ScClkTck);
        return value > 0 ? value : 100;
    }

    /// <summary>Nanoseconds per clock tick, precomputed for the CPU maths.</summary>
    internal static ulong NanosPerTick { get; } = (ulong)(1_000_000_000L / ClockTicksPerSecond);

    // ---- Directory counting -------------------------------------------------
    //
    // Counting /proc/PID/fd for every process is the single most expensive part
    // of a sweep. Going through the BCL's enumerator costs a string allocation
    // and an attribute lookup per entry, which measured at ~114 ms across ~600
    // processes — most of a sweep's budget spent on a number we could get by just
    // counting directory entries. opendir/readdir does exactly that and nothing
    // more.

    [LibraryImport("libc", EntryPoint = "opendir", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint OpenDir(string name);

    [LibraryImport("libc", EntryPoint = "readdir")]
    private static partial nint ReadDir(nint dir);

    [LibraryImport("libc", EntryPoint = "closedir")]
    private static partial int CloseDir(nint dir);

    /// <summary>
    /// Offset of <c>d_name</c> within <c>struct dirent</c> on Linux:
    /// <c>d_ino</c> (8) + <c>d_off</c> (8) + <c>d_reclen</c> (2) + <c>d_type</c> (1).
    /// Stable across glibc and musl, both of which mirror the kernel's
    /// <c>linux_dirent64</c>.
    /// </summary>
    private const int DirentNameOffset = 19;

    /// <summary>
    /// Count entries in a directory, excluding <c>.</c> and <c>..</c>. Returns
    /// null when the directory cannot be opened, which for <c>/proc/PID/fd</c>
    /// means another user's process.
    /// </summary>
    internal static unsafe int? CountDirectoryEntries(string path)
    {
        var dir = OpenDir(path);
        if (dir == nint.Zero)
        {
            return null;
        }

        try
        {
            var count = 0;
            nint entry;
            while ((entry = ReadDir(dir)) != nint.Zero)
            {
                var name = (byte*)(entry + DirentNameOffset);

                // Skip "." and ".." without materialising a string.
                if (name[0] == (byte)'.' &&
                    (name[1] == 0 || (name[1] == (byte)'.' && name[2] == 0)))
                {
                    continue;
                }

                count++;
            }

            return count;
        }
        finally
        {
            CloseDir(dir);
        }
    }
}
