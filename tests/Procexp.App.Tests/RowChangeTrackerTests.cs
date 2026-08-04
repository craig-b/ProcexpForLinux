using Procexp.App.Controls;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// The lower pane's new/gone tracking. Watching a process load and unload
/// libraries, or open and close descriptors, is one of the main things the pane
/// is for, and those events are invisible without the highlighting.
/// </summary>
public class RowChangeTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstObservationEstablishesABaseline()
    {
        var tracker = new RowChangeTracker<string>();

        var gone = tracker.Observe(["a", "b", "c"], T0);

        Assert.Empty(gone);
        Assert.False(tracker.IsNew("a"));
        Assert.False(tracker.IsNew("b"));
    }

    [Fact]
    public void ArrivalsAreFlaggedNew()
    {
        var tracker = new RowChangeTracker<string>();

        tracker.Observe(["a"], T0);
        tracker.Observe(["a", "b"], T0);

        Assert.True(tracker.IsNew("b"));
        Assert.False(tracker.IsNew("a"));
    }

    [Fact]
    public void DeparturesAreReportedSoTheyCanBeKeptOnScreen()
    {
        var tracker = new RowChangeTracker<string>();

        tracker.Observe(["a", "b"], T0);
        var gone = tracker.Observe(["a"], T0);

        Assert.Equal(["b"], gone);
        Assert.True(tracker.IsGone("b"));
    }

    [Fact]
    public void HighlightsExpire()
    {
        var tracker = new RowChangeTracker<string> { HighlightDuration = TimeSpan.FromSeconds(2) };

        tracker.Observe(["a"], T0);
        tracker.Observe(["a", "b"], T0);
        Assert.True(tracker.IsNew("b"));

        Assert.True(tracker.Expire(T0 + TimeSpan.FromSeconds(3)));
        Assert.False(tracker.IsNew("b"));
    }

    [Fact]
    public void ExpireReportsNoChangeWhenNothingIsStale()
    {
        var tracker = new RowChangeTracker<string> { HighlightDuration = TimeSpan.FromSeconds(5) };

        tracker.Observe(["a"], T0);
        tracker.Observe(["a", "b"], T0);

        Assert.False(tracker.Expire(T0 + TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// A file descriptor number is reused almost immediately after being closed,
    /// so a key that departs and returns must read as new rather than staying
    /// tinted as gone.
    /// </summary>
    [Fact]
    public void AKeyThatReturnsIsNewRatherThanStillGone()
    {
        var tracker = new RowChangeTracker<int>();

        tracker.Observe([3, 4], T0);
        tracker.Observe([3], T0);
        Assert.True(tracker.IsGone(4));

        tracker.Observe([3, 4], T0 + TimeSpan.FromMilliseconds(500));

        Assert.False(tracker.IsGone(4));
        Assert.True(tracker.IsNew(4));
    }

    [Fact]
    public void ResetClearsEverything()
    {
        var tracker = new RowChangeTracker<string>();

        tracker.Observe(["a"], T0);
        tracker.Observe(["a", "b"], T0);
        Assert.True(tracker.IsNew("b"));

        tracker.Reset();

        Assert.False(tracker.IsNew("b"));

        // After a reset the next observation is a baseline again, so switching
        // process does not report every row of the new one as new.
        Assert.Empty(tracker.Observe(["x", "y"], T0));
        Assert.False(tracker.IsNew("x"));
    }

    [Fact]
    public void DisablingTrackingReportsNothing()
    {
        var tracker = new RowChangeTracker<string> { IsEnabled = false };

        tracker.Observe(["a"], T0);
        var gone = tracker.Observe(["b"], T0);

        Assert.Empty(gone);
        Assert.False(tracker.IsNew("b"));
    }

    [Fact]
    public void ColourFollowsTheProcessListLegend()
    {
        var tracker = new RowChangeTracker<string>();
        var rules = ProcessColorRule.Defaults;

        tracker.Observe(["a", "b"], T0);
        tracker.Observe(["a", "c"], T0);

        var newColour = ProcessColorRule.Background(
            ProcessFlags.NewProcess,
            rules,
            darkMode: false
        );
        var deadColour = ProcessColorRule.Background(
            ProcessFlags.DeadProcess,
            rules,
            darkMode: false
        );

        Assert.Equal(newColour, tracker.Colour("c", rules, darkMode: false));
        Assert.Equal(deadColour, tracker.Colour("b", rules, darkMode: false));
        Assert.Null(tracker.Colour("a", rules, darkMode: false));
    }
}
