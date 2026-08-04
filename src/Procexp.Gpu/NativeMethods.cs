using System.Runtime.InteropServices;

namespace Procexp.Gpu;

internal static partial class NativeMethods
{
    [LibraryImport("libc", EntryPoint = "readlink", SetLastError = true)]
    private static unsafe partial nint ReadLink(byte* path, byte* buffer, nuint size);

    /// <summary>
    /// Test whether a file descriptor points at a DRM device, without allocating.
    /// </summary>
    /// <remarks>
    /// This is the pre-filter that makes per-process GPU sampling affordable. Out
    /// of roughly 30,000 open descriptors on a desktop, about 47 are DRM clients —
    /// so opening and reading every fdinfo file to find them wastes three syscalls
    /// on each of the other 29,950. A readlink is one syscall and answers the
    /// question directly.
    /// </remarks>
    internal static unsafe bool IsDrmDescriptor(string procPath)
    {
        const int MaxPath = 256;

        Span<byte> pathBytes = stackalloc byte[MaxPath];
        var written = System.Text.Encoding.UTF8.GetBytes(procPath, pathBytes);
        if (written >= MaxPath)
        {
            return false;
        }

        pathBytes[written] = 0;

        Span<byte> target = stackalloc byte[MaxPath];

        nint length;
        fixed (byte* pathPointer = pathBytes)
        fixed (byte* targetPointer = target)
        {
            length = ReadLink(pathPointer, targetPointer, MaxPath);
        }

        if (length <= 0)
        {
            return false;
        }

        // readlink does not NUL-terminate, so compare against the returned length.
        return target[..(int)length].StartsWith("/dev/dri/"u8);
    }
}
