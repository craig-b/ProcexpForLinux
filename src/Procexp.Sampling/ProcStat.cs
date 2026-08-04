namespace Procexp.Sampling;

/// <summary>
/// The fields of <c>/proc/PID/stat</c> that we care about.
/// </summary>
/// <remarks>
/// Field numbering follows proc(5), which is 1-based and counts <c>comm</c> as
/// field 2.
/// </remarks>
internal struct ProcStatFields
{
    internal int Pid;
    internal string Comm;
    internal char State;
    internal int Ppid;
    internal int TtyNr;
    internal uint Flags;
    internal ulong MinorFaults;
    internal ulong MajorFaults;
    internal ulong UserTimeTicks;
    internal ulong SystemTimeTicks;
    internal int Priority;
    internal int Nice;
    internal int NumThreads;
    internal ulong StartTimeTicks;
    internal ulong VirtualSize;
    internal long ResidentPages;
    internal int Processor;
    internal int RealtimePriority;
    internal int Policy;
    internal int SessionId;
}

internal static class ProcStat
{
    /// <summary>
    /// Parse <c>/proc/PID/stat</c>.
    /// </summary>
    /// <remarks>
    /// The <c>comm</c> field is the reason this cannot be a naive split: it is
    /// the raw thread name wrapped in parentheses, and the kernel does not escape
    /// it. A process named <c>foo bar) baz</c> is entirely legal and is a
    /// classic way to confuse parsers — so we locate the <em>last</em> close
    /// paren rather than the first, and only split on spaces after it.
    /// </remarks>
    internal static bool TryParse(ReadOnlySpan<byte> content, out ProcStatFields fields)
    {
        fields = default;

        var open = content.IndexOf((byte)'(');
        var close = content.LastIndexOf((byte)')');
        if (open < 0 || close < 0 || close < open)
        {
            return false;
        }

        fields.Pid = ProcFile.ParseInt32(content[..open]);
        fields.Comm = ProcFile.ToString(content[(open + 1)..close]);

        // Everything after ") " is space-separated and free of surprises.
        var rest = content[(close + 1)..];
        if (!rest.IsEmpty && rest[0] == (byte)' ')
        {
            rest = rest[1..];
        }

        // Field 3: state.
        var field = ProcFile.NextField(ref rest);
        fields.State = field.IsEmpty ? '\0' : (char)field[0];

        fields.Ppid = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 4
        ProcFile.NextField(ref rest); // 5  pgrp
        fields.SessionId = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 6  session
        fields.TtyNr = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 7
        ProcFile.NextField(ref rest); // 8  tpgid
        fields.Flags = (uint)ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 9
        fields.MinorFaults = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 10
        ProcFile.NextField(ref rest); // 11 cminflt
        fields.MajorFaults = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 12
        ProcFile.NextField(ref rest); // 13 cmajflt
        fields.UserTimeTicks = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 14
        fields.SystemTimeTicks = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 15
        ProcFile.NextField(ref rest); // 16 cutime
        ProcFile.NextField(ref rest); // 17 cstime
        fields.Priority = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 18
        fields.Nice = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 19
        fields.NumThreads = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 20
        ProcFile.NextField(ref rest); // 21 itrealvalue
        fields.StartTimeTicks = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 22
        fields.VirtualSize = ProcFile.ParseUInt64(ProcFile.NextField(ref rest)); // 23
        fields.ResidentPages = ProcFile.ParseInt64(ProcFile.NextField(ref rest)); // 24

        // 25..38 are limits, memory layout, and signal masks we do not surface.
        rest = ProcFile.SkipFields(rest, 14);

        fields.Processor = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 39
        fields.RealtimePriority = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 40
        fields.Policy = ProcFile.ParseInt32(ProcFile.NextField(ref rest)); // 41

        return true;
    }

    /// <summary>
    /// Decode the <c>tty_nr</c> device number into a friendly terminal name.
    /// </summary>
    /// <remarks>
    /// The encoding splits the minor number across two ranges of bits, which is a
    /// historical artefact of dev_t widening.
    /// </remarks>
    internal static string? DecodeTty(int ttyNr)
    {
        if (ttyNr == 0)
        {
            return null;
        }

        var major = (ttyNr >> 8) & 0xFF;
        var minor = (ttyNr & 0xFF) | ((ttyNr >> 12) & 0xFFF00);

        return major switch
        {
            // Pseudo-terminal slaves: the common case for terminal emulators and ssh.
            >= 136 and <= 143 => $"pts/{minor + (major - 136) * 256}",
            4 when minor < 64 => $"tty{minor}",
            4 => $"ttyS{minor - 64}",
            5 when minor == 0 => "tty",
            5 when minor == 1 => "console",
            _ => $"{major}:{minor}",
        };
    }
}
