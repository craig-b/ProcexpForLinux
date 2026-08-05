using Procexp.Sampling;
using Xunit;

namespace Procexp.Sampling.Tests;

/// <summary>
/// The security fields of <c>/proc/PID/status</c>, read against this very
/// process — the values are knowable independently, so a parser slip shows up
/// as a disagreement with the runtime rather than as plausible nonsense.
/// </summary>
public class ProcSecurityTests
{
    [Fact]
    public void OwnUidsMatchTheRuntime()
    {
        var info = ProcSecurity.Read(Environment.ProcessId);

        Assert.NotNull(info.Uids);
        Assert.Equal((uint)NativeUid(), info.Uids!.Real);

        // A test runner is not setuid, so the four ids agree.
        Assert.Equal(info.Uids.Real, info.Uids.Effective);
        Assert.Equal(info.Uids.Real, info.Uids.Saved);
    }

    [Fact]
    public void CapabilityMasksAreRead()
    {
        var info = ProcSecurity.Read(Environment.ProcessId);

        // The bounding set is never empty, even unprivileged: it is what the
        // process could ever gain, not what it holds.
        Assert.NotNull(info.CapabilitiesBounding);
        Assert.NotEqual(0UL, info.CapabilitiesBounding!.Value);
        Assert.NotNull(info.CapabilitiesEffective);
    }

    [Theory]
    [InlineData(0UL, new string[0])]
    [InlineData(1UL << 0, new[] { "CAP_CHOWN" })]
    [InlineData(1UL << 21, new[] { "CAP_SYS_ADMIN" })]
    [InlineData((1UL << 5) | (1UL << 23), new[] { "CAP_KILL", "CAP_SYS_NICE" })]
    public void CapabilityNamesDecode(ulong mask, string[] expected) =>
        Assert.Equal(expected, ProcSecurity.DescribeCapabilities(mask));

    /// <summary>
    /// A bit past the known table is still reported, since a newer kernel
    /// defining CAP_42 must not silently vanish from the list.
    /// </summary>
    [Fact]
    public void UnknownCapabilityBitsAreReportedByNumber() =>
        Assert.Equal(["CAP_60"], ProcSecurity.DescribeCapabilities(1UL << 60));

    [Theory]
    [InlineData(0, 0, "disabled")]
    [InlineData(1, 0, "strict")]
    [InlineData(2, 0, "filtered")]
    [InlineData(2, 3, "filtered (3 filters)")]
    public void SeccompModesDescribe(int mode, int filters, string expected) =>
        Assert.Equal(expected, ProcSecurity.DescribeSeccomp(mode, filters));

    [Fact]
    public void MissingProcessYieldsEmptyInfoRatherThanThrowing()
    {
        var info = ProcSecurity.Read(-1);
        Assert.Null(info.Uids);
        Assert.Empty(info.Groups);
    }

    private static int NativeUid() =>
        int.Parse(
            File.ReadAllLines($"/proc/{Environment.ProcessId}/status")
                .First(l => l.StartsWith("Uid:", StringComparison.Ordinal))
                .Split('\t', StringSplitOptions.RemoveEmptyEntries)[1],
            System.Globalization.CultureInfo.InvariantCulture
        );
}
