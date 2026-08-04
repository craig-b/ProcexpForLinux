using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Procexp.Helper;

/// <summary>Kernel-supplied identity of the process at the other end of a socket.</summary>
internal readonly record struct PeerCredentials(int Pid, uint Uid, uint Gid);

/// <summary>
/// Reads <c>SO_PEERCRED</c> — the connecting process's pid, uid and gid as
/// recorded by the kernel at connect time.
/// </summary>
/// <remarks>
/// This replaces the macOS peer validation, which checks the client's code
/// signature. There is no Linux equivalent and there could not be: any binary a
/// user runs carries that user's authority, so validating <em>which</em> binary
/// connected buys nothing — an attacker who can run code as an authorised user
/// can equally run the real client.
///
/// So authorisation here is by identity, not by image: filesystem permissions on
/// the socket are the actual gate, and these credentials exist to enforce a
/// second check and to make the audit log meaningful. They cannot be forged;
/// the kernel fills them in and the peer never gets to state them.
/// </remarks>
internal static partial class PeerCredentialReader
{
    private const int SolSocket = 1;
    private const int SoPeercred = 17;

    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static unsafe partial int GetSockOpt(int socket, int level, int name, void* value, int* length);

    /// <summary>Read the peer's credentials, or null if the kernel will not say.</summary>
    internal static unsafe PeerCredentials? Read(Socket socket)
    {
        // struct ucred { pid_t pid; uid_t uid; gid_t gid; } — three 32-bit fields.
        Span<int> credentials = stackalloc int[3];
        var length = sizeof(int) * 3;

        int result;
        fixed (int* pointer = credentials)
        {
            result = GetSockOpt((int)socket.Handle, SolSocket, SoPeercred, pointer, &length);
        }

        if (result != 0 || length < sizeof(int) * 3)
        {
            return null;
        }

        return new PeerCredentials(credentials[0], (uint)credentials[1], (uint)credentials[2]);
    }
}
