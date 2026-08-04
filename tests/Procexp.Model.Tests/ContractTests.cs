using Xunit;

namespace Procexp.Model.Tests;

/// <summary>
/// Equivalents of the macOS <c>ProcexpModelTests</c> suite, adapted where the
/// Linux contracts deliberately differ.
/// </summary>
public class ContractTests
{
    private static ProcessRecord Record(ProcessId id, string name, ProcessId? parent = null) =>
        new() { Id = id, Name = name, Parent = parent };

    [Fact]
    public void TreeBuilder_ComputesRootsAndChildren_PromotingOrphans()
    {
        var root = new ProcessId(1, 0);
        var child = new ProcessId(2, 0);
        var orphan = new ProcessId(3, 0);

        var processes = new Dictionary<ProcessId, ProcessRecord>
        {
            [root] = Record(root, "systemd"),
            [child] = Record(child, "child", root),
            // Parent 99 is absent from the map: the parent exited and the kernel
            // has not yet reparented this process. It must surface as a root
            // rather than vanish from the tree.
            [orphan] = Record(orphan, "orphan", new ProcessId(99, 0)),
        };

        var (roots, children) = ProcessTreeBuilder.Build(processes);

        Assert.Contains(root, roots);
        Assert.Contains(orphan, roots);
        Assert.Equal([child], children[root]);
    }

    [Fact]
    public void SnapshotDiff_DetectsAddedRemovedAndChanged()
    {
        var id1 = new ProcessId(1, 10);
        var id2 = new ProcessId(2, 20);
        var id3 = new ProcessId(3, 30);

        var old = Snapshot(new Dictionary<ProcessId, ProcessRecord>
        {
            [id1] = Record(id1, "one") with { CpuPercent = 1 },
            [id2] = Record(id2, "two"),
        });

        var @new = Snapshot(new Dictionary<ProcessId, ProcessRecord>
        {
            [id1] = Record(id1, "one") with { CpuPercent = 2 },
            [id3] = Record(id3, "three"),
        });

        var diff = SnapshotDiff.Between(old, @new);

        Assert.Equal([id3], diff.Added);
        Assert.Equal([id2], diff.Removed);
        Assert.Equal([id1], diff.Changed);
    }

    private static ProcessSnapshot Snapshot(IReadOnlyDictionary<ProcessId, ProcessRecord> processes)
    {
        var (roots, children) = ProcessTreeBuilder.Build(processes);
        return new ProcessSnapshot
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Interval = 1,
            Processes = processes,
            Roots = roots,
            Children = children,
            System = SystemStats.Zero,
        };
    }

    [Fact]
    public void HistoryRing_KeepsNewestSamplesOldestFirst()
    {
        var ring = new HistoryRing<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            ring.Append(i);
        }

        Assert.Equal([3, 4, 5], ring.Values);
        Assert.Equal(5, ring.Latest);
        Assert.Equal(3, ring.Count);
    }

    [Fact]
    public void HistoryRing_RejectsNonPositiveCapacity() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryRing<int>(0));

    [Theory]
    [InlineData(0ul, "")]
    [InlineData(512ul, "512 B")]
    [InlineData(2ul * 1024 * 1024, "2.0 M")]
    [InlineData(1024ul, "1.0 K")]
    public void ByteFormatting_IsCompactAndLocaleStable(ulong value, string expected) =>
        Assert.Equal(expected, ValueFormat.Bytes(value));

    [Fact]
    public void Duration_FormatsAsHoursMinutesSecondsCentis()
    {
        Assert.Equal("0:00:02.00", ValueFormat.Duration(2_000_000_000));
        Assert.Equal("1:01:01.50", ValueFormat.Duration((3600UL + 61) * 1_000_000_000 + 500_000_000));
    }

    [Fact]
    public void Columns_FormatAndProduceSortKeys()
    {
        var p = new ProcessRecord
        {
            Id = new ProcessId(1234, 0),
            Name = "firefox",
            CpuPercent = 12.5,
            ThreadCount = 8,
            ResidentSize = 100 * 1024 * 1024,
        };

        Assert.Equal("1234", Columns.Format(Column.Pid, p));
        Assert.Equal("firefox", Columns.Format(Column.Name, p));
        Assert.Equal("12.50", Columns.Format(Column.Cpu, p));
        Assert.Equal("8", Columns.Format(Column.Threads, p));
        Assert.Equal("100.0 M", Columns.Format(Column.WorkingSet, p));
        Assert.Equal(0, Columns.SortValue(Column.Cpu, p).CompareTo(SortKey.Number(12.5)));
    }

    [Fact]
    public void Columns_LinuxSpecificFieldsFormat()
    {
        var p = new ProcessRecord
        {
            Id = new ProcessId(4321, 0),
            Name = "tool",
            State = 'D',
            KernelFlags = 0x40,
            SchedulingPolicy = 1,
            RunningThreadCount = 3,
            MinorFaults = 7,
            MajorFaults = 11,
            VoluntaryContextSwitches = 19,
            OomScore = 42,
            SystemdUnit = "tool.service",
        };

        Assert.Equal("3", Columns.Format(Column.RunningThreads, p));
        Assert.Equal("Disk Sleep", Columns.Format(Column.State, p));
        Assert.Equal("FIFO", Columns.Format(Column.SchedulingPolicy, p));
        Assert.Equal("0x00000040", Columns.Format(Column.KernelFlags, p));
        Assert.Equal("7", Columns.Format(Column.MinorFaults, p));
        Assert.Equal("42", Columns.Format(Column.OomScore, p));
        Assert.Equal("tool.service", Columns.Format(Column.SystemdUnit, p));
    }

    /// <summary>
    /// The macOS build gates a batch of columns on a <c>limitedTaskInfo</c> flag.
    /// Linux expresses the same thing through nullability: a field the kernel
    /// would not let us read stays null and renders blank, so a restricted
    /// process is visibly distinct from one that genuinely did no I/O.
    /// </summary>
    [Fact]
    public void RestrictedFields_RenderBlankRatherThanZero()
    {
        var p = new ProcessRecord
        {
            Id = new ProcessId(99, 0),
            Name = "other-user",
            Flags = ProcessFlags.LimitedInfo,
        };

        Assert.Equal("", Columns.Format(Column.IoRead, p));
        Assert.Equal("", Columns.Format(Column.IoWrite, p));
        Assert.Equal("", Columns.Format(Column.PrivateBytes, p));
        Assert.Equal(0, Columns.SortValue(Column.IoRead, p).CompareTo(SortKey.None));
        Assert.False(p.HasFullInfo);
    }

    [Fact]
    public void SortKey_NoneSortsLastRegardlessOfType()
    {
        Assert.True(SortKey.None.CompareTo(SortKey.Number(0)) > 0);
        Assert.True(SortKey.Number(double.MaxValue).CompareTo(SortKey.None) < 0);
        Assert.True(SortKey.None.CompareTo(SortKey.Text("zzz")) > 0);
        Assert.Equal(0, SortKey.None.CompareTo(SortKey.None));
    }

    /// <summary>
    /// GPU is unsupported on macOS but available here through DRM fdinfo.
    /// Network is not: Linux has no per-process byte counter, so the column stays
    /// unsupported exactly as it is on macOS. Sockets still enumerate — it is only
    /// the rate that has no source.
    /// </summary>
    [Fact]
    public void SupportedColumns_IncludeGpuButNotNetworkRate()
    {
        var supported = Columns.Supported.ToHashSet();

        Assert.Contains(Column.Gpu, supported);
        Assert.Contains(Column.CommandLine, supported);
        Assert.DoesNotContain(Column.Network, supported);
        Assert.DoesNotContain(Column.GpuMemory, supported);
    }

    [Fact]
    public void PinnedColumns_AreNameAndPid()
    {
        Assert.Equal([Column.Name, Column.Pid], Columns.Pinned);
        Assert.Contains(Column.Pid, Columns.Supported);
    }

    [Fact]
    public void LowerPaneColumnDefaults_MirrorProcessExplorer()
    {
        Assert.Equal(
            [ModuleColumn.Name, ModuleColumn.Description, ModuleColumn.Company, ModuleColumn.Path],
            ModuleColumns.Default);
        Assert.Equal([HandleColumn.Kind, HandleColumn.Name, HandleColumn.Fd], HandleColumns.Default);
        Assert.Equal([ModuleColumn.Name], ModuleColumns.Required);
        Assert.Equal([ThreadColumn.Tid], ThreadColumns.Required);
    }

    [Fact]
    public void LowerPaneColumnMetadata_SuppliesTitlesWidthsAndAlignment()
    {
        Assert.True(ModuleColumns.IsRightAligned(ModuleColumn.Base));
        Assert.Equal("Path", ModuleColumns.Title(ModuleColumn.Path));
        Assert.Equal("FD", HandleColumns.Title(HandleColumn.Fd));
        Assert.True(HandleColumns.IsRightAligned(HandleColumn.Fd));
        Assert.True(ThreadColumns.DefaultWidth(ThreadColumn.State) > 0);
    }

    /// <summary>
    /// wchan is the genuine Linux equivalent of Process Explorer's Wait Reason
    /// column, which the macOS build had to omit for want of a public API.
    /// </summary>
    [Fact]
    public void ThreadColumns_IncludeWaitReason()
    {
        Assert.Equal("Wait Reason", ThreadColumns.Title(ThreadColumn.WaitChannel));
        Assert.Contains(ThreadColumn.WaitChannel, ThreadColumns.Default);
    }

    [Fact]
    public void NewAndDeadColors_TakePriorityOverOwnProcess()
    {
        var background = ProcessColorRule.Background(
            ProcessFlags.OwnProcess | ProcessFlags.NewProcess,
            ProcessColorRule.Defaults,
            darkMode: false);

        var expected = ProcessColorRule.Defaults.First(r => r.Flag == ProcessFlags.NewProcess).BackgroundLight;
        Assert.Equal(expected, background);
    }

    [Fact]
    public void NoMatchingRule_LeavesDefaultBackground() =>
        Assert.Null(ProcessColorRule.Background(ProcessFlags.None, ProcessColorRule.Defaults, darkMode: false));

    [Fact]
    public void ProcessIdentity_SurvivesPidReuse()
    {
        var a = new ProcessId(500, 100);
        var b = new ProcessId(500, 200);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Provenance_DisplayNameSummarisesPackage()
    {
        var verified = new ProvenanceInfo
        {
            Status = ProvenanceStatus.PackageVerified,
            Repository = "core",
            PackageName = "coreutils",
            PackageVersion = "9.5-1",
        };
        Assert.Equal("core/coreutils 9.5-1", verified.DisplayName);

        var modified = new ProvenanceInfo
        {
            Status = ProvenanceStatus.PackageModified,
            PackageName = "coreutils",
        };
        Assert.Equal("coreutils (modified)", modified.DisplayName);

        Assert.Equal("(unpackaged)", new ProvenanceInfo { Status = ProvenanceStatus.Unpackaged }.DisplayName);
        Assert.Equal("", ProvenanceInfo.Unverified.DisplayName);
    }
}
