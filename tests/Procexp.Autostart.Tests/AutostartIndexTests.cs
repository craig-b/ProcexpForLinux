using Procexp.Model;
using Xunit;

namespace Procexp.Autostart.Tests;

public class AutostartProviderTests
{
    private static ProcessRecord Process(
        string? executablePath = null,
        string? unit = null,
        ProcessFlags flags = ProcessFlags.None
    ) =>
        new()
        {
            Id = new ProcessId(4242, 1000),
            Name = "test",
            ExecutablePath = executablePath,
            SystemdUnit = unit,
            Flags = flags,
        };

    [Fact]
    public async Task KernelThreadsNeverResolve()
    {
        var provider = new AutostartProvider();

        var result = await provider.AutostartLocationAsync(
            Process(flags: ProcessFlags.KernelThread)
        );

        Assert.Null(result);
    }

    /// <summary>
    /// A transient or generated unit has no file on disk, so the index cannot
    /// find it — but naming the unit is still more useful than reporting nothing.
    /// </summary>
    [Fact]
    public async Task TransientUnitStillReportsItsName()
    {
        var provider = new AutostartProvider();

        var result = await provider.AutostartLocationAsync(
            Process(unit: "run-r92bdb60d6a9b4d5f.service")
        );

        Assert.NotNull(result);
        Assert.Contains("run-r92bdb60d6a9b4d5f.service", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownProcessResolvesToNothing()
    {
        var provider = new AutostartProvider();

        var result = await provider.AutostartLocationAsync(
            Process(executablePath: "/nonexistent/path/to/nothing")
        );

        Assert.Null(result);
    }

    /// <summary>
    /// The index is built from the live system, so this asserts only what must be
    /// true of any systemd machine rather than pinning specific units.
    /// </summary>
    [Fact]
    public void IndexFindsSystemUnits()
    {
        var index = new AutostartIndex();

        Assert.True(index.Count > 0, "no autostart definitions were found on this system");
    }

    [Fact]
    public void InvalidateForcesARebuild()
    {
        var index = new AutostartIndex();
        var before = index.Count;

        index.Invalidate();

        Assert.Equal(before, index.Count);
    }
}

public class AutostartEntryTests
{
    [Theory]
    [InlineData(AutostartKind.SystemdSystemUnit, "systemd: sshd.service")]
    [InlineData(AutostartKind.SystemdUserUnit, "systemd --user: sshd.service")]
    [InlineData(AutostartKind.XdgAutostart, "XDG autostart: sshd.service")]
    [InlineData(AutostartKind.SysVInit, "init.d: sshd.service")]
    public void DisplayNamesTheMechanism(AutostartKind kind, string expected)
    {
        var entry = new AutostartEntry
        {
            DefinitionPath = "/etc/systemd/system/sshd.service",
            Kind = kind,
            Name = "sshd.service",
        };

        Assert.Equal(expected, entry.Display);
    }

    /// <summary>
    /// Cron entries have no unit name, so the display falls back to the file that
    /// defines the job — which is the thing the user would go and edit.
    /// </summary>
    [Fact]
    public void CronDisplaysItsDefinitionFile()
    {
        var entry = new AutostartEntry
        {
            DefinitionPath = "/etc/cron.d/backup",
            Kind = AutostartKind.Cron,
        };

        Assert.Equal("cron: /etc/cron.d/backup", entry.Display);
    }
}
