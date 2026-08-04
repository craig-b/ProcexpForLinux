using System.Text;
using Procexp.Model;
using Xunit;

namespace Procexp.Sampling.Tests;

public class CgroupTests
{
    private static CgroupClassification Parse(string content) =>
        CgroupInfo.Parse(Encoding.UTF8.GetBytes(content));

    [Fact]
    public void ClassifiesSystemService()
    {
        var result = Parse("0::/system.slice/sshd.service\n");

        Assert.True(result.IsService);
        Assert.Equal("sshd.service", result.Unit);
        Assert.Equal("/system.slice/sshd.service", result.Path);
    }

    /// <summary>
    /// A user session process is not a system service, even though it lives under
    /// a .service cgroup — user@1000.service is the per-user manager, not the
    /// thing that launched the process.
    /// </summary>
    [Fact]
    public void UserSessionIsNotASystemService()
    {
        var result = Parse(
            "0::/user.slice/user-1000.slice/user@1000.service/app.slice/app-foo.scope\n"
        );

        Assert.False(result.IsService);
        Assert.Equal("app-foo.scope", result.Unit);
    }

    [Fact]
    public void UserManagerItselfIsSkippedWhenLookingForTheUnit()
    {
        // Nothing but user@1000.service in the path: there is no launching unit
        // to report, so the answer must be null rather than the manager.
        var result = Parse("0::/user.slice/user-1000.slice/user@1000.service\n");

        Assert.Null(result.Unit);
    }

    /// <summary>
    /// On a hybrid system both hierarchies are mounted and both emit lines. The
    /// unified (v2) line has an empty controller list and must win, since the v1
    /// paths are per-controller and disagree with each other.
    /// </summary>
    [Fact]
    public void UnifiedHierarchyWinsOverLegacyControllers()
    {
        var result = Parse(
            "12:pids:/legacy/pids/path\n"
                + "5:memory:/legacy/memory/path\n"
                + "0::/system.slice/correct.service\n"
        );

        Assert.Equal("/system.slice/correct.service", result.Path);
        Assert.Equal("correct.service", result.Unit);
    }

    [Fact]
    public void FallsBackToLegacyWhenNoUnifiedLine()
    {
        var result = Parse("5:memory:/system.slice/legacy.service\n");
        Assert.Equal("/system.slice/legacy.service", result.Path);
    }

    [Theory]
    [InlineData("0::/system.slice/docker-abc123.scope\n", ImageKind.Container)]
    [InlineData("0::/kubepods/besteffort/pod123/abc\n", ImageKind.Container)]
    [InlineData("0::/machine.slice/libpod-abc.scope\n", ImageKind.Container)]
    [InlineData("0::/user.slice/user-1000.slice/snap.firefox.scope\n", ImageKind.Snap)]
    [InlineData("0::/user.slice/app-flatpak-org.gnome.Calculator-123.scope\n", ImageKind.Flatpak)]
    [InlineData("0::/system.slice/plain.service\n", ImageKind.Unknown)]
    public void DetectsContainerAndSandboxKinds(string content, ImageKind expected) =>
        Assert.Equal(expected, Parse(content).ContainerKind);

    [Theory]
    [InlineData("")]
    [InlineData("0::/\n")]
    public void RootOrEmptyCgroupYieldsNothing(string content)
    {
        var result = Parse(content);
        Assert.Null(result.Path);
        Assert.False(result.IsService);
    }
}

public class ProcMapsTests
{
    private static List<ModuleInfo> Parse(string content) =>
        ProcMaps.Parse(Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// A shared library is mapped as several segments with different
    /// permissions. Process Explorer shows one row per module, so they must be
    /// folded together into a single span.
    /// </summary>
    [Fact]
    public void FoldsSegmentsOfOneFileIntoOneModule()
    {
        var modules = Parse(
            "7f0000001000-7f0000002000 r--p 00000000 08:01 100 /usr/lib/libc.so.6\n"
                + "7f0000002000-7f0000005000 r-xp 00001000 08:01 100 /usr/lib/libc.so.6\n"
                + "7f0000005000-7f0000006000 rw-p 00004000 08:01 100 /usr/lib/libc.so.6\n"
        );

        var module = Assert.Single(modules);
        Assert.Equal("/usr/lib/libc.so.6", module.Path);
        Assert.Equal("libc.so.6", module.Name);
        Assert.Equal(0x7f0000001000UL, module.LoadAddress);
        Assert.Equal(0x5000UL, module.Size);

        // The permissions shown are the union across segments, so the row
        // reflects that the file is mapped executable somewhere.
        Assert.Contains('r', module.Permissions);
        Assert.Contains('x', module.Permissions);
        Assert.Contains('w', module.Permissions);
    }

    [Fact]
    public void SkipsAnonymousMappings()
    {
        var modules = Parse(
            "7f0000001000-7f0000002000 rw-p 00000000 00:00 0 \n"
                + "7f0000003000-7f0000004000 rw-p 00000000 00:00 0\n"
                + "7f0000005000-7f0000006000 r-xp 00000000 08:01 100 /usr/bin/tool\n"
        );

        Assert.Single(modules);
        Assert.Equal("/usr/bin/tool", modules[0].Path);
    }

    [Fact]
    public void KeepsSpecialRegionsThatAreNamed()
    {
        // [heap] and [stack] are named, so they survive the anonymous filter and
        // appear in the list, as they do in Process Explorer's DLL view.
        var modules = Parse(
            "01000000-01021000 rw-p 00000000 00:00 0 [heap]\n"
                + "7ffd00000000-7ffd00021000 rw-p 00000000 00:00 0 [stack]\n"
        );

        Assert.Equal(2, modules.Count);
        Assert.Contains(modules, m => m.Path == "[heap]");
    }

    [Fact]
    public void HandlesPathsContainingSpaces()
    {
        var modules = Parse(
            "7f0000001000-7f0000002000 r-xp 00000000 08:01 100 /opt/My App/lib my.so\n"
        );

        Assert.Equal("/opt/My App/lib my.so", Assert.Single(modules).Path);
    }

    [Fact]
    public void PreservesFirstSeenOrder()
    {
        var modules = Parse(
            "1000-2000 r-xp 0 08:01 1 /a.so\n"
                + "3000-4000 r-xp 0 08:01 2 /b.so\n"
                + "5000-6000 r--p 0 08:01 1 /a.so\n"
        );

        Assert.Equal(["/a.so", "/b.so"], modules.Select(m => m.Path));
    }

    [Fact]
    public void IgnoresMalformedLines()
    {
        var modules = Parse(
            "not a map line\n" + "7f0000001000-7f0000002000 r-xp 00000000 08:01 100 /usr/bin/tool\n"
        );

        Assert.Equal("/usr/bin/tool", Assert.Single(modules).Path);
    }
}

public class FileDescriptorTests
{
    /// <summary>
    /// fdinfo reports open flags in octal. Decoding them as decimal or hex
    /// silently mislabels every descriptor's access mode.
    /// </summary>
    [Theory]
    [InlineData(0u, "Read")]
    [InlineData(1u, "Write")]
    [InlineData(2u, "Read/Write")]
    [InlineData(0x400u, "Read, Append")]
    [InlineData(0x800u, "Read, Non-blocking")]
    [InlineData(0x80000u, "Read, Close-on-exec")]
    [InlineData(0x80002u, "Read/Write, Close-on-exec")]
    public void DecodesAccessMode(uint flags, string expectedPrefix)
    {
        var decoded = ProcFileDescriptors.DecodeAccess(flags);
        Assert.NotNull(decoded);
        Assert.StartsWith(expectedPrefix, decoded);
    }

    [Fact]
    public void NullFlagsDecodeToNothing() => Assert.Null(ProcFileDescriptors.DecodeAccess(null));
}
