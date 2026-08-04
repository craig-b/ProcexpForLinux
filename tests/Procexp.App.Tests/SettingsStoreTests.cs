using Procexp.App.Settings;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// Settings files outlive the code that wrote them, so the loader has to cope
/// with anything an older version, a newer version, or a hand edit could leave
/// behind.
/// </summary>
public class SettingsStoreTests
{
    [Fact]
    public void PinnedColumnsAreAlwaysPresentAndFirst()
    {
        var normalised = Columns_.Normalise([Column.WorkingSet, Column.Cpu]);

        Assert.Equal(Column.Name, normalised[0]);
        Assert.Equal(Column.Pid, normalised[1]);
        Assert.Contains(Column.WorkingSet, normalised);
    }

    /// <summary>
    /// Without the pinned columns the tree has nothing to draw a row with, so a
    /// file that omits them must be repaired rather than honoured.
    /// </summary>
    [Fact]
    public void MissingPinnedColumnsAreRestored()
    {
        var normalised = Columns_.Normalise([Column.Cpu, Column.Threads]);

        Assert.Contains(Column.Name, normalised);
        Assert.Contains(Column.Pid, normalised);
    }

    [Fact]
    public void DuplicatesAreCollapsed()
    {
        var normalised = Columns_.Normalise(
            [Column.Cpu, Column.Cpu, Column.Name, Column.Name, Column.WorkingSet]);

        Assert.Equal(normalised.Count, normalised.Distinct().Count());
    }

    /// <summary>
    /// A column supported by a future version, or one removed in this one, must
    /// not survive into the layout — the renderer would have no data for it.
    /// </summary>
    [Fact]
    public void UnsupportedColumnsAreDropped()
    {
        var normalised = Columns_.Normalise([Column.Name, Column.Network, Column.GpuMemory, Column.Cpu]);

        Assert.DoesNotContain(Column.Network, normalised);
        Assert.DoesNotContain(Column.GpuMemory, normalised);
        Assert.Contains(Column.Cpu, normalised);
    }

    [Fact]
    public void OrderOfChosenColumnsIsPreserved()
    {
        var normalised = Columns_.Normalise(
            [Column.Name, Column.Pid, Column.WorkingSet, Column.Cpu, Column.Threads]);

        var workingSet = normalised.ToList().IndexOf(Column.WorkingSet);
        var cpu = normalised.ToList().IndexOf(Column.Cpu);

        Assert.True(workingSet < cpu, "user ordering must survive normalisation");
    }

    [Fact]
    public void EmptyListStillYieldsAUsableLayout()
    {
        var normalised = Columns_.Normalise([]);

        Assert.Equal(Columns.Pinned.Count, normalised.Count);
        Assert.Equal(Columns.Pinned, normalised);
    }

    [Fact]
    public void DefaultsRoundTripThroughTheStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"procexp-settings-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);

            var settings = AppSettings.Defaults with
            {
                Columns = [Column.Name, Column.Pid, Column.Cpu],
                SortColumn = Column.WorkingSet,
                SortDescending = false,
                RefreshSeconds = 2,
                NamePaneWidth = 320,
            };

            SettingsStore.Save(settings);
            var loaded = SettingsStore.Load();

            Assert.Equal(Column.WorkingSet, loaded.SortColumn);
            Assert.False(loaded.SortDescending);
            Assert.Equal(2, loaded.RefreshSeconds);
            Assert.Equal(320, loaded.NamePaneWidth);
            Assert.Contains(Column.Cpu, loaded.Columns);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was written.
            }
        }
    }

    /// <summary>
    /// A truncated or hand-mangled file must not stop the app starting. Refusing
    /// to launch over a settings file would be a poor trade.
    /// </summary>
    [Fact]
    public void CorruptFileFallsBackToDefaults()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"procexp-settings-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Directory.CreateDirectory(Path.Combine(directory, "procexp"));
            File.WriteAllText(Path.Combine(directory, "procexp", "settings.json"), "{ \"Columns\": [ truncated");

            var loaded = SettingsStore.Load();

            Assert.Equal(AppSettings.Defaults.SortColumn, loaded.SortColumn);
            Assert.Equal(AppSettings.Defaults.RefreshSeconds, loaded.RefreshSeconds);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was written.
            }
        }
    }

    /// <summary>Absurd values from a hand edit are clamped rather than obeyed.</summary>
    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"procexp-settings-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);

            SettingsStore.Save(AppSettings.Defaults with
            {
                RefreshSeconds = 0.0001,
                NamePaneWidth = 99999,
                WindowWidth = 1,
                WindowHeight = -50,
            });

            var loaded = SettingsStore.Load();

            Assert.InRange(loaded.RefreshSeconds, 0.25, 60);
            Assert.InRange(loaded.NamePaneWidth, 120, 900);
            Assert.True(loaded.WindowWidth >= 640);
            Assert.True(loaded.WindowHeight >= 480);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was written.
            }
        }
    }
}
