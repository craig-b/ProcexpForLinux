using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// The image-lookup path selection, which decides whether the Description,
/// Company and Verified Signer columns fill in at all.
/// </summary>
public class ProcessEnricherTests
{
    private static ProcessRecord Record(string? exe = null, string? cmdline = null) =>
        new()
        {
            Id = new ProcessId(42, 100),
            Name = "test",
            ExecutablePath = exe,
            CommandLine = cmdline,
        };

    [Fact]
    public void PrefersTheResolvedExecutablePath() =>
        Assert.Equal("/usr/bin/ls", ProcessEnricher.LookupPathFor(
            Record(exe: "/usr/bin/ls", cmdline: "/something/else --flag")));

    /// <summary>
    /// readlink on /proc/PID/exe is ptrace-gated, so every process owned by
    /// another user arrives with no executable path. Without this fallback the
    /// metadata columns stay blank for exactly the system processes a user most
    /// wants identified.
    /// </summary>
    [Fact]
    public void FallsBackToArgvZeroWhenTheExecutableIsUnreadable() =>
        Assert.Equal("/usr/bin/ls", ProcessEnricher.LookupPathFor(
            Record(cmdline: "/usr/bin/ls -la /tmp")));

    [Fact]
    public void UsesTheWholeCommandLineWhenItHasNoArguments() =>
        Assert.Equal("/usr/bin/ls", ProcessEnricher.LookupPathFor(Record(cmdline: "/usr/bin/ls")));

    /// <summary>
    /// Daemons routinely rewrite argv[0] into a status line — avahi-daemon
    /// reports "avahi-daemon: running [host.local]". Treating that as a path
    /// would send the package manager on a fruitless lookup on every refresh.
    /// </summary>
    [Theory]
    [InlineData("avahi-daemon: running [craig.local]")]
    [InlineData("sshd: craig [priv]")]
    [InlineData("relative/path/binary")]
    [InlineData("bash")]
    [InlineData("")]
    public void RejectsArgvZeroThatIsNotAnExistingAbsolutePath(string cmdline) =>
        Assert.Null(ProcessEnricher.LookupPathFor(Record(cmdline: cmdline)));

    [Fact]
    public void RejectsAnAbsolutePathThatDoesNotExist() =>
        Assert.Null(ProcessEnricher.LookupPathFor(
            Record(cmdline: "/nonexistent/binary --flag")));

    [Fact]
    public void KernelThreadsWithNeitherYieldNothing() =>
        Assert.Null(ProcessEnricher.LookupPathFor(Record()));
}
