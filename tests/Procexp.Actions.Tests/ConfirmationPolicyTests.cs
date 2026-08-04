using Procexp.Model;
using Xunit;

namespace Procexp.Actions.Tests;

public class ConfirmationPolicyTests
{
    /// <summary>
    /// The uid the tests run under. Fixtures must use it, because a process
    /// belonging to a <em>different</em> user is disruptive by policy — so
    /// leaving Uid at its default of 0 would make every "ordinary process" case
    /// silently exercise the cross-user branch instead.
    /// </summary>
    private static readonly uint OwnUid = ReadOwnUid();

    private static uint ReadOwnUid()
    {
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                var fields = line[4..].Split('\t', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 1 && uint.TryParse(fields[1].Trim(), out var uid))
                {
                    return uid;
                }
            }
        }

        return 0;
    }

    private static ProcessRecord Process(
        int pid, string name, ProcessFlags flags = ProcessFlags.None, string? unit = null) =>
        new()
        {
            Id = new ProcessId(pid, 1000),
            Name = name,
            Uid = OwnUid,
            Flags = flags,
            SystemdUnit = unit,
        };

    [Fact]
    public void KillingInitIsRefused()
    {
        var confirmation = ActionConfirmationPolicy.ForKill(Process(1, "systemd"));

        Assert.True(confirmation.IsRefused);
        Assert.Equal(ConfirmationSeverity.Critical, confirmation.Severity);
    }

    [Fact]
    public void KillingOurselvesIsRefused()
    {
        var confirmation = ActionConfirmationPolicy.ForKill(
            Process(Environment.ProcessId, "procexp"));

        Assert.True(confirmation.IsRefused);
    }

    /// <summary>
    /// Session and display managers are rated critical but not refused: taking
    /// down your own desktop is a legitimate thing to ask for, unlike halting the
    /// machine.
    /// </summary>
    [Theory]
    [InlineData("gnome-shell")]
    [InlineData("Xorg")]
    [InlineData("sddm")]
    [InlineData("kwin_wayland")]
    [InlineData("systemd-logind")]
    public void SessionCriticalProcessesWarnLoudlyButAreAllowed(string name)
    {
        var confirmation = ActionConfirmationPolicy.ForKill(Process(4242, name));

        Assert.Equal(ConfirmationSeverity.Critical, confirmation.Severity);
        Assert.False(confirmation.IsRefused);
    }

    [Fact]
    public void ServicesWarnThatSystemdWillRestartThem()
    {
        var confirmation = ActionConfirmationPolicy.ForKill(
            Process(900, "sshd", ProcessFlags.Service, "sshd.service"));

        Assert.Equal(ConfirmationSeverity.Disruptive, confirmation.Severity);
        Assert.False(confirmation.IsRefused);
        Assert.Contains("sshd.service", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("restart", confirmation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryProcessIsRoutine()
    {
        var confirmation = ActionConfirmationPolicy.ForKill(Process(5000, "sleep"));

        Assert.Equal(ConfirmationSeverity.Routine, confirmation.Severity);
        Assert.False(confirmation.IsRefused);
    }

    /// <summary>
    /// Another user's process needs elevated rights, so it warns even though the
    /// process itself is unremarkable.
    /// </summary>
    [Fact]
    public void AnotherUsersProcessIsDisruptive()
    {
        var other = Process(5000, "sleep") with { Uid = OwnUid + 1, UserName = "someone-else" };
        var confirmation = ActionConfirmationPolicy.ForKill(other);

        // Running as root removes the distinction, so only assert when we are not.
        if (OwnUid != 0)
        {
            Assert.Equal(ConfirmationSeverity.Disruptive, confirmation.Severity);
            Assert.Contains("someone-else", confirmation.Message, StringComparison.Ordinal);
        }

        Assert.False(confirmation.IsRefused);
    }

    [Fact]
    public void KillTreeSaysSoInTheMessage()
    {
        var confirmation = ActionConfirmationPolicy.ForKill(Process(5000, "make"), isTree: true);

        Assert.Contains("descended", confirmation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Kill Process Tree", confirmation.Title);
    }

    /// <summary>
    /// Suspending init would freeze the machine with no way back, so it is
    /// refused outright rather than merely warned about.
    /// </summary>
    [Fact]
    public void SuspendingInitIsRefused() =>
        Assert.True(ActionConfirmationPolicy.ForSuspend(Process(1, "systemd")).IsRefused);

    [Fact]
    public void SuspendingASessionManagerWarnsAboutBeingUnableToResume()
    {
        var confirmation = ActionConfirmationPolicy.ForSuspend(Process(4242, "gnome-shell"));

        Assert.Equal(ConfirmationSeverity.Critical, confirmation.Severity);
        Assert.Contains("resume", confirmation.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Restarting a unit here relaunches its command line without the
    /// environment, capabilities or cgroup systemd would supply, so the message
    /// has to point at systemctl rather than quietly doing something subtly
    /// different.
    /// </summary>
    [Fact]
    public void RestartingAServiceRecommendsSystemctl()
    {
        var confirmation = ActionConfirmationPolicy.ForRestart(
            Process(900, "nginx", ProcessFlags.Service, "nginx.service"));

        Assert.Contains("systemctl restart nginx.service", confirmation.Message, StringComparison.Ordinal);
        Assert.Equal(ConfirmationSeverity.Disruptive, confirmation.Severity);
    }
}

public class ProcessActionsTests
{
    /// <summary>
    /// The identity guard must reject a stale identity before any signal is sent.
    /// pid 1 is used as the target precisely because a bug here would be
    /// catastrophic and unmistakable — the guard is what stands between a stale
    /// UI row and signalling init.
    /// </summary>
    [Fact]
    public void StaleIdentityIsRefusedBeforeSignalling()
    {
        var actions = new ProcessActions();

        // A start time that cannot match: pid 1 started at boot.
        var stale = new ProcessId(1, ulong.MaxValue);

        var exception = Assert.Throws<ProviderException>(() => actions.Signal(stale, Signals.Cont));
        Assert.Equal(ProviderErrorKind.ProcessGone, exception.Kind);
    }

    [Fact]
    public void NonexistentProcessIsReportedGone()
    {
        var actions = new ProcessActions();

        // Above the default pid_max, so it cannot exist.
        var missing = new ProcessId(0x7FFF_FFFE, 12345);

        var exception = Assert.Throws<ProviderException>(() => actions.Signal(missing, Signals.Cont));
        Assert.Equal(ProviderErrorKind.ProcessGone, exception.Kind);
    }

    /// <summary>
    /// A zero start time means the caller built the identity without one, so
    /// there is nothing to compare and the guard has to let it through. The
    /// helper deliberately does not accept this — see ProcessIdentity there.
    /// </summary>
    [Fact]
    public void ZeroStartTimeSkipsTheComparison()
    {
        var actions = new ProcessActions();

        // Signal 0 checks for existence without delivering anything.
        var exception = Record.Exception(() => actions.Signal(new ProcessId(Environment.ProcessId, 0), 0));
        Assert.Null(exception);
    }
}
