using Procexp.Model;
using Xunit;

namespace Procexp.Net.Tests;

public class ProcNetTests
{
    /// <summary>
    /// <c>/proc/net/tcp</c> writes addresses as native-endian 32-bit words
    /// rendered in hex, not as byte strings. On a little-endian machine
    /// 127.0.0.1 therefore appears as 0100007F. Getting this wrong is easy to
    /// miss because palindromic addresses decode identically either way.
    /// </summary>
    [Theory]
    [InlineData("0100007F:0035", "127.0.0.1", 53)]
    [InlineData("00000000:1F90", "0.0.0.0", 8080)]
    [InlineData("0101A8C0:01BB", "192.168.1.1", 443)]
    [InlineData("FFFFFFFF:FFFF", "255.255.255.255", 65535)]
    public void DecodesIpv4Endpoints(string field, string expectedAddress, int expectedPort)
    {
        var socket = ParseSingleTcpRow(field, "00000000:0000", "0A");

        Assert.Equal(expectedAddress, socket.LocalAddress);
        Assert.Equal((ushort)expectedPort, socket.LocalPort);
    }

    /// <summary>
    /// A deliberately asymmetric address, which decodes to something obviously
    /// different if the word swap is missing.
    /// </summary>
    [Fact]
    public void AsymmetricAddressProvesTheWordSwap()
    {
        var socket = ParseSingleTcpRow("0201A8C0:0050", "00000000:0000", "0A");

        Assert.Equal("192.168.1.2", socket.LocalAddress);
        Assert.NotEqual("2.1.168.192", socket.LocalAddress);
    }

    /// <summary>
    /// IPv6 is four 32-bit words in sequence, each swapped independently — not
    /// one 128-bit value reversed end to end.
    /// </summary>
    [Fact]
    public void DecodesIpv6Loopback()
    {
        var socket = ParseSingleTcpRow(
            "00000000000000000000000001000000:0050", "00000000000000000000000000000000:0000", "0A");

        Assert.Equal("::1", socket.LocalAddress);
    }

    [Theory]
    [InlineData(1, "ESTABLISHED")]
    [InlineData(2, "SYN_SENT")]
    [InlineData(6, "TIME_WAIT")]
    [InlineData(10, "LISTEN")]
    [InlineData(11, "CLOSING")]
    public void DecodesTcpStates(byte state, string expected) =>
        Assert.Equal(expected, ProcNetTables.TcpStateName(state));

    [Fact]
    public void UnknownTcpStateFallsBackToItsNumber() =>
        Assert.Equal("99", ProcNetTables.TcpStateName(99));

    [Fact]
    public void ParsesQueuesAndUid()
    {
        var socket = ParseSingleTcpRow("0100007F:0050", "0100007F:D431", "01",
            queues: "0000002A:00000010", uid: "1000", inode: "987654");

        Assert.Equal(0x10u, socket.ReceiveQueue);
        Assert.Equal(0x2Au, socket.SendQueue);
        Assert.Equal(1000u, socket.Uid);
        Assert.Equal(987654UL, socket.Inode);
        Assert.Equal("ESTABLISHED", socket.State);
        Assert.Equal(54321, socket.RemotePort);
    }

    /// <summary>
    /// A socket with inode 0 is not owned by any process — it is a TIME_WAIT
    /// remnant the kernel still tracks. Including it would attribute a phantom
    /// socket to whichever process happens to hold fd 0.
    /// </summary>
    [Fact]
    public void SkipsRowsWithNoInode()
    {
        var table = WriteAndParse(
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
            "   0: 0100007F:0050 00000000:0000 06 00000000:00000000 00:00000000 00000000     0        0 0\n");

        Assert.Empty(table);
    }

    [Fact]
    public void SkipsHeaderAndMalformedRows()
    {
        var table = WriteAndParse(
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
            "nonsense\n" +
            "   0: 0100007F:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 555\n");

        Assert.Single(table);
        Assert.Equal(555UL, table.Values.Single().Inode);
    }

    // ---- helpers ------------------------------------------------------------

    private static SocketInfo ParseSingleTcpRow(
        string local,
        string remote,
        string state,
        string queues = "00000000:00000000",
        string uid = "0",
        string inode = "123456")
    {
        var table = WriteAndParse(
            "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
            $"   0: {local} {remote} {state} {queues} 00:00000000 00000000 {uid,5}        0 {inode}\n");

        return Assert.Single(table).Value;
    }

    /// <summary>
    /// The parser reads from a path, so fixtures go through a temporary file.
    /// That keeps the file-format handling — headers, column offsets, blank
    /// lines — under test rather than only the field decoding.
    /// </summary>
    private static Dictionary<ulong, SocketInfo> WriteAndParse(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"procexp-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(path, content);
            return ProcNetTables.ReadInetTableForTesting(path, SocketProtocol.Tcp, isTcp: true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
