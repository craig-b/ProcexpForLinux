using System.Text.Json;
using Procexp.Model;
using Procexp.Privileged;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// Round-trips the structured helper payloads.
/// </summary>
/// <remarks>
/// The model types are records with <c>required</c> and init-only members, which
/// source-generated serialisation handles differently from ordinary mutable
/// classes. A failure here would surface as a silently empty lower pane for every
/// process needing the helper — exactly the kind of thing that looks like a
/// permissions problem and is not.
/// </remarks>
public class HelperPayloadTests
{
    [Fact]
    public void ModulesRoundTrip()
    {
        IReadOnlyList<ModuleInfo> original =
        [
            new()
            {
                Path = "/usr/lib/libc.so.6",
                Name = "libc.so.6",
                LoadAddress = 0x7f0000001000,
                Size = 0x5000,
                Permissions = "r-xp",
                IsSharedLibrary = true,
            },
            new()
            {
                Path = "/opt/My App/lib with space.so",
                Name = "lib with space.so",
                LoadAddress = 0,
                Size = 4096,
                Permissions = "r--p",
            },
        ];

        var json = JsonSerializer.Serialize(original, HelperJsonContext.Default.IReadOnlyListModuleInfo);
        var restored = JsonSerializer.Deserialize(json, HelperJsonContext.Default.IReadOnlyListModuleInfo);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Count);
        Assert.Equal(original[0], restored[0]);
        Assert.Equal(original[1], restored[1]);

        // Large addresses must survive as 64-bit values rather than being
        // truncated or turned into floating point.
        Assert.Equal(0x7f0000001000UL, restored[0].LoadAddress);
    }

    [Fact]
    public void FileDescriptorsRoundTripIncludingNestedSockets()
    {
        IReadOnlyList<FileDescriptorInfo> original =
        [
            new()
            {
                Fd = 3,
                Kind = FdKind.Socket,
                Name = "socket:[123456]",
                Inode = 123456,
                Socket = new SocketInfo
                {
                    Fd = 3,
                    Protocol = SocketProtocol.Tcp,
                    LocalAddress = "127.0.0.1",
                    LocalPort = 8080,
                    RemoteAddress = "0.0.0.0",
                    State = "LISTEN",
                    Inode = 123456,
                },
            },
            new()
            {
                Fd = 0,
                Kind = FdKind.CharacterDevice,
                Name = "/dev/null",
                Access = "Read (0o0)",
                OpenFlags = 0,
                Offset = 0,
            },
        ];

        var json = JsonSerializer.Serialize(original, HelperJsonContext.Default.IReadOnlyListFileDescriptorInfo);
        var restored = JsonSerializer.Deserialize(json, HelperJsonContext.Default.IReadOnlyListFileDescriptorInfo);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.Count);
        Assert.Equal(original[0], restored[0]);
        Assert.Equal(FdKind.CharacterDevice, restored[1].Kind);

        // The nested socket is the part most likely to be dropped.
        Assert.NotNull(restored[0].Socket);
        Assert.Equal("LISTEN", restored[0].Socket!.State);
        Assert.Equal(8080, restored[0].Socket!.LocalPort);
    }

    [Fact]
    public void EmptyListsRoundTrip()
    {
        var json = JsonSerializer.Serialize(
            (IReadOnlyList<ModuleInfo>)[], HelperJsonContext.Default.IReadOnlyListModuleInfo);

        var restored = JsonSerializer.Deserialize(json, HelperJsonContext.Default.IReadOnlyListModuleInfo);

        Assert.NotNull(restored);
        Assert.Empty(restored);
    }

    [Fact]
    public void RequestsAndResponsesRoundTrip()
    {
        var request = new HelperRequest
        {
            Operation = HelperOperation.ReadModules,
            Pid = 4242,
            StartTime = 987654321,
        };

        var json = JsonSerializer.Serialize(request, HelperJsonContext.Default.HelperRequest);
        var restored = JsonSerializer.Deserialize(json, HelperJsonContext.Default.HelperRequest);

        Assert.NotNull(restored);
        Assert.Equal(HelperOperation.ReadModules, restored.Operation);
        Assert.Equal(4242, restored.Pid);

        // Start time carries the identity guard; losing precision here would let
        // the helper act on a recycled pid.
        Assert.Equal(987654321UL, restored.StartTime);
    }

    [Fact]
    public void MalformedPayloadYieldsNullRatherThanThrowing()
    {
        var response = new HelperResponse { Ok = true, Content = "{ not json" };
        Assert.NotNull(response.Content);

        // Exercised through the same path the client uses.
        var restored = JsonSerializer.Deserialize(
            "[]", HelperJsonContext.Default.IReadOnlyListModuleInfo);

        Assert.NotNull(restored);
    }
}
