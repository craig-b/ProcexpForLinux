using System.Globalization;
using System.Net;
using Procexp.Model;

namespace Procexp.Net;

/// <summary>
/// The kernel's socket tables, from <c>/proc/net/{tcp,tcp6,udp,udp6,unix}</c>,
/// indexed by socket inode.
/// </summary>
/// <remarks>
/// Inode is the join key: <c>/proc/PID/fd</c> entries for sockets are symlinks
/// reading <c>socket:[12345]</c>, and that number is the inode listed here. It is
/// the only thing tying a socket to the process that owns it — the socket tables
/// themselves name a uid but never a pid.
/// </remarks>
internal static class ProcNetTables
{
    internal static Dictionary<ulong, SocketInfo> ReadAll()
    {
        var result = new Dictionary<ulong, SocketInfo>();

        ReadInetTable("/proc/net/tcp", SocketProtocol.Tcp, isTcp: true, result);
        ReadInetTable("/proc/net/tcp6", SocketProtocol.Tcp6, isTcp: true, result);
        ReadInetTable("/proc/net/udp", SocketProtocol.Udp, isTcp: false, result);
        ReadInetTable("/proc/net/udp6", SocketProtocol.Udp6, isTcp: false, result);
        ReadUnixTable("/proc/net/unix", result);

        return result;
    }

    /// <summary>
    /// Parse a single table from an arbitrary path. Exists so the tests can drive
    /// the parser over fixture files instead of whatever /proc/net happens to
    /// contain on the machine running them.
    /// </summary>
    internal static Dictionary<ulong, SocketInfo> ReadInetTableForTesting(
        string path,
        SocketProtocol protocol,
        bool isTcp
    )
    {
        var result = new Dictionary<ulong, SocketInfo>();
        ReadInetTable(path, protocol, isTcp, result);
        return result;
    }

    /// <summary>
    /// Parse one of the IPv4/IPv6 tables. Columns are
    /// <c>sl local rem st tx:rx tr:when retrnsmt uid timeout inode</c>.
    /// </summary>
    private static void ReadInetTable(
        string path,
        SocketProtocol protocol,
        bool isTcp,
        Dictionary<ulong, SocketInfo> into
    )
    {
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // IPv6 tables are absent on kernels built without it.
            return;
        }

        var isHeader = true;
        foreach (var line in lines)
        {
            if (isHeader)
            {
                isHeader = false;
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10)
            {
                continue;
            }

            if (
                !TryParseEndpoint(fields[1], out var localAddress, out var localPort)
                || !TryParseEndpoint(fields[2], out var remoteAddress, out var remotePort)
            )
            {
                continue;
            }

            if (
                !byte.TryParse(
                    fields[3],
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var state
                )
            )
            {
                continue;
            }

            var queues = fields[4].Split(':');
            uint? txQueue = null;
            uint? rxQueue = null;
            if (queues.Length == 2)
            {
                if (
                    uint.TryParse(
                        queues[0],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var tx
                    )
                )
                {
                    txQueue = tx;
                }

                if (
                    uint.TryParse(
                        queues[1],
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var rx
                    )
                )
                {
                    rxQueue = rx;
                }
            }

            _ = uint.TryParse(fields[7], out var uid);

            if (!ulong.TryParse(fields[9], out var inode) || inode == 0)
            {
                continue;
            }

            into[inode] = new SocketInfo
            {
                Fd = -1, // filled in when joined to a process
                Protocol = protocol,
                LocalAddress = localAddress,
                LocalPort = localPort,
                RemoteAddress = remoteAddress,
                RemotePort = remotePort,
                State = isTcp ? TcpStateName(state) : "",
                TcpStateRaw = isTcp ? state : null,
                Inode = inode,
                Uid = uid,
                SendQueue = txQueue,
                ReceiveQueue = rxQueue,
            };
        }
    }

    /// <summary>
    /// Parse a <c>HEXADDR:HEXPORT</c> endpoint.
    /// </summary>
    /// <remarks>
    /// The address is written as native-endian 32-bit words rendered in hex, not
    /// as a byte string — so on a little-endian machine 127.0.0.1 appears as
    /// <c>0100007F</c>. IPv6 is four such words in sequence. Reversing each
    /// 4-byte group individually is what makes both cases come out right.
    /// </remarks>
    private static bool TryParseEndpoint(string field, out string address, out ushort port)
    {
        address = "";
        port = 0;

        var colon = field.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var addressHex = field.AsSpan(0, colon);
        var portHex = field.AsSpan(colon + 1);

        if (
            !ushort.TryParse(
                portHex,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out port
            )
        )
        {
            return false;
        }

        if (addressHex.Length is not (8 or 32))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[addressHex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (
                !byte.TryParse(
                    addressHex.Slice(i * 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out bytes[i]
                )
            )
            {
                return false;
            }
        }

        // Byte-swap within each 32-bit word.
        for (var offset = 0; offset < bytes.Length; offset += 4)
        {
            bytes.Slice(offset, 4).Reverse();
        }

        try
        {
            address = new IPAddress(bytes).ToString();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Parse <c>/proc/net/unix</c>. Columns are
    /// <c>Num RefCount Protocol Flags Type St Inode Path</c>, where the path is
    /// absent for unnamed socket pairs.
    /// </summary>
    private static void ReadUnixTable(string path, Dictionary<ulong, SocketInfo> into)
    {
        IEnumerable<string> lines;
        try
        {
            lines = File.ReadLines(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var isHeader = true;
        foreach (var line in lines)
        {
            if (isHeader)
            {
                isHeader = false;
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 7 || !ulong.TryParse(fields[6], out var inode) || inode == 0)
            {
                continue;
            }

            // An abstract socket's name begins with a NUL, which the kernel
            // renders as '@'.
            var socketPath = fields.Length > 7 ? fields[7] : null;

            into[inode] = new SocketInfo
            {
                Fd = -1,
                Protocol = SocketProtocol.Unix,
                State = UnixStateName(fields[5]),
                Inode = inode,
                UnixPath = socketPath,
                LocalAddress = socketPath ?? "",
            };
        }
    }

    private static string UnixStateName(string hex) =>
        byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var state)
            ? state switch
            {
                1 => "UNCONNECTED",
                2 => "CONNECTING",
                3 => "CONNECTED",
                4 => "DISCONNECTING",
                _ => "",
            }
            : "";

    internal static string TcpStateName(byte state) =>
        state switch
        {
            1 => "ESTABLISHED",
            2 => "SYN_SENT",
            3 => "SYN_RECV",
            4 => "FIN_WAIT1",
            5 => "FIN_WAIT2",
            6 => "TIME_WAIT",
            7 => "CLOSE",
            8 => "CLOSE_WAIT",
            9 => "LAST_ACK",
            10 => "LISTEN",
            11 => "CLOSING",
            12 => "NEW_SYN_RECV",
            _ => state.ToString(CultureInfo.InvariantCulture),
        };
}
