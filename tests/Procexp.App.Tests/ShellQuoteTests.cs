using Procexp.App.Dialogs;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// Quoting for paths that reach <c>sh -c</c> from the Browse button.
/// </summary>
/// <remarks>
/// The path is not text the user typed — it is a filename, and a filename is
/// attacker-controllable in any directory that receives downloads. Double
/// quotes would leave command substitution live, so these cases are the
/// difference between naming a file and running it.
/// </remarks>
public class ShellQuoteTests
{
    [Theory]
    [InlineData("/usr/bin/htop", "'/usr/bin/htop'")]
    [InlineData("/home/a/My Programs/run", "'/home/a/My Programs/run'")]
    [InlineData("/tmp/$(rm -rf ~).sh", "'/tmp/$(rm -rf ~).sh'")]
    [InlineData("/tmp/`id`.sh", "'/tmp/`id`.sh'")]
    [InlineData("/tmp/$HOME.sh", "'/tmp/$HOME.sh'")]
    [InlineData("/tmp/a;reboot", "'/tmp/a;reboot'")]
    public void DangerousCharactersAreInert(string path, string expected) =>
        Assert.Equal(expected, RunDialog.ShellQuote(path));

    /// <summary>
    /// The one character single quotes cannot carry: close, escape, reopen.
    /// </summary>
    [Fact]
    public void SingleQuoteIsClosedAndReopened() =>
        Assert.Equal("'/tmp/it'\\''s here'", RunDialog.ShellQuote("/tmp/it's here"));

    /// <summary>
    /// A quote-escape crafted to end the quoting must stay inside it.
    /// </summary>
    [Fact]
    public void QuoteFollowedByCommandStaysQuoted()
    {
        var quoted = RunDialog.ShellQuote("/tmp/x';reboot;'");
        Assert.Equal("'/tmp/x'\\'';reboot;'\\'''", quoted);
    }
}
