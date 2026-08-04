using System.Text;
using Xunit;

namespace Procexp.Sampling.Tests;

/// <summary>
/// Tests for the <c>/proc/PID/stat</c> parser, over fixture text rather than the
/// live filesystem so these run identically in CI.
/// </summary>
public class ProcStatTests
{
    private static bool Parse(string content, out ProcStatFields fields) =>
        ProcStat.TryParse(Encoding.UTF8.GetBytes(content), out fields);

    /// <summary>
    /// A realistic line, for the ordinary case. Field order after comm is
    /// state, ppid, pgrp, session, tty, tpgid, flags, minflt, <em>cminflt</em>,
    /// majflt, cmajflt, utime, stime — the cumulative child counters interleave
    /// with the process's own, which is easy to mis-count by one.
    /// </summary>
    private const string SystemdStat =
        "1 (systemd) S 0 1 1 0 -1 4194560 45678 891011 120 340 250 830 1500 2900 20 0 1 0 "
        + "42 24096768 3521 18446744073709551615 1 1 0 0 0 0 671173123 4096 1260 0 0 0 17 3 0 0 0 0 0 "
        + "0 0 0 0 0 0 0 0 0";

    [Fact]
    public void ParsesOrdinaryProcess()
    {
        Assert.True(Parse(SystemdStat, out var fields));

        Assert.Equal(1, fields.Pid);
        Assert.Equal("systemd", fields.Comm);
        Assert.Equal('S', fields.State);
        Assert.Equal(0, fields.Ppid);
        Assert.Equal(1, fields.SessionId);
        Assert.Equal(0, fields.TtyNr);
        Assert.Equal(45678UL, fields.MinorFaults);
        Assert.Equal(120UL, fields.MajorFaults); // not 891011, which is cminflt
        Assert.Equal(250UL, fields.UserTimeTicks);
        Assert.Equal(830UL, fields.SystemTimeTicks);
        Assert.Equal(20, fields.Priority);
        Assert.Equal(0, fields.Nice);
        Assert.Equal(1, fields.NumThreads);
        Assert.Equal(42UL, fields.StartTimeTicks);
        Assert.Equal(24096768UL, fields.VirtualSize);
        Assert.Equal(3521L, fields.ResidentPages);
    }

    /// <summary>
    /// The comm field is the raw thread name wrapped in parentheses, and the
    /// kernel does not escape it. A process can legitimately be named something
    /// containing spaces and parens, which is the classic way a naive split-on-
    /// space parser silently mis-reads every subsequent field.
    /// </summary>
    [Theory]
    [InlineData("(evil) name)", "evil) name")]
    [InlineData("(has space)", "has space")]
    [InlineData("(((nested)))", "((nested))")]
    [InlineData("(S 99 99 99)", "S 99 99 99")]
    [InlineData("()", "")]
    public void ParsesAdversarialProcessNames(string comm, string expectedName)
    {
        var content =
            $"4242 {comm} R 7 7 7 0 -1 0 1 2 3 4 100 200 0 0 20 0 5 0 "
            + "999 1024 64 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 3 0 0";

        Assert.True(Parse(content, out var fields));

        Assert.Equal(4242, fields.Pid);
        Assert.Equal(expectedName, fields.Comm);

        // Everything after comm must still line up. If the parser had split on
        // the first close paren, these would be garbage.
        Assert.Equal('R', fields.State);
        Assert.Equal(7, fields.Ppid);
        Assert.Equal(100UL, fields.UserTimeTicks);
        Assert.Equal(200UL, fields.SystemTimeTicks);
        Assert.Equal(5, fields.NumThreads);
        Assert.Equal(999UL, fields.StartTimeTicks);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage with no parens")]
    [InlineData("1 (unclosed S 0")]
    public void RejectsMalformedInput(string content) => Assert.False(Parse(content, out _));

    [Fact]
    public void ParsesSchedulingFieldsFromTheTail()
    {
        // Fields 39, 40, 41: processor, rt_priority, policy.
        var content =
            "500 (worker) R 1 500 500 0 -1 0 0 0 0 0 10 20 0 0 20 0 1 0 "
            + "1000 1024 64 "
            +
            // 25..38 — limits, memory layout, signal masks
            "0 0 0 0 0 0 0 0 0 0 0 0 0 0 "
            + "11 50 2";

        Assert.True(Parse(content, out var fields));

        Assert.Equal(11, fields.Processor);
        Assert.Equal(50, fields.RealtimePriority);
        Assert.Equal(2, fields.Policy);
    }

    /// <summary>
    /// tty_nr splits the minor number across two disjoint bit ranges, a leftover
    /// of dev_t widening. Reading it as a contiguous field gets pts numbers wrong
    /// above 255.
    /// </summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(34816, "pts/0")] // major 136, minor 0
    [InlineData(34817, "pts/1")] // major 136, minor 1
    [InlineData(1025, "tty1")] // major 4, minor 1
    [InlineData(1088, "ttyS0")] // major 4, minor 64
    public void DecodesTerminalDevices(int ttyNr, string? expected) =>
        Assert.Equal(expected, ProcStat.DecodeTty(ttyNr));
}
