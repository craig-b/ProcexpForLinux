using Procexp.App.Settings;
using Procexp.Model;
using Xunit;

namespace Procexp.App.Tests;

/// <summary>
/// Loading a settings file written by an older build.
/// </summary>
/// <remarks>
/// Settings outlive the code that wrote them, so every release meets files
/// missing whatever it added. A reference-typed property absent from the JSON
/// arrives null rather than as its initialiser, and the first thing to use it
/// throws — which is a crash on startup, before any window exists to report
/// it, and unfixable from inside the app. The store promises loading never
/// throws; these are that promise.
/// </remarks>
[Collection("settings")]
public class SettingsUpgradeTests
{
    /// <summary>Exactly the keys a v0.1-era file has: no colour rules, no confirmations.</summary>
    private const string OldFormat = """
        {
          "Columns": ["Name", "Pid", "Cpu", "PrivateBytes"],
          "ColumnWidths": { "Name": 260, "Pid": 78 },
          "ColumnSets": [],
          "SortColumn": "Cpu",
          "SortDescending": true,
          "TreeMode": true,
          "ShowLowerPane": true,
          "LowerPaneMode": "Modules",
          "RefreshSeconds": 1,
          "HighlightNewAndDead": true,
          "AlwaysOnTop": false,
          "NamePaneWidth": 260,
          "WindowWidth": 1200,
          "WindowHeight": 760
        }
        """;

    /// <summary>Every collection explicitly null, which a hand-edit can produce.</summary>
    private const string NullCollections = """
        {
          "Columns": null,
          "ColumnWidths": null,
          "ColumnSets": null,
          "ColorRules": null
        }
        """;

    private static AppSettings LoadFrom(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"procexp-upgrade-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");

        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", directory);
            Directory.CreateDirectory(Path.Combine(directory, "procexp"));
            File.WriteAllText(SettingsStore.SettingsPath, json);

            return SettingsStore.Load();
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Temp directory; leaving it is harmless.
            }
        }
    }

    [Fact]
    public void OlderFileLoadsWithoutNullCollections()
    {
        var loaded = LoadFrom(OldFormat);

        Assert.NotNull(loaded.ColorRules);
        Assert.NotNull(loaded.ColumnSets);
        Assert.NotNull(loaded.Columns);
        Assert.NotNull(loaded.ColumnWidths);

        // The settings it did carry must survive the repair.
        Assert.Equal(Column.Cpu, loaded.SortColumn);
        Assert.Contains(Column.Cpu, loaded.Columns);
    }

    [Fact]
    public void OlderFileYieldsUsableColourRules()
    {
        var rules = ColorRuleSetting.ToRules(LoadFrom(OldFormat).ColorRules);
        Assert.Equal(ProcessColorRule.Defaults.Count, rules.Count);
    }

    [Fact]
    public void ExplicitNullsAreRepaired()
    {
        var loaded = LoadFrom(NullCollections);

        Assert.NotEmpty(loaded.Columns);
        Assert.NotNull(loaded.ColumnWidths);
        Assert.NotNull(loaded.ColumnSets);
        Assert.NotNull(loaded.ColorRules);
    }

    /// <summary>The conversions themselves must not throw on a null list.</summary>
    [Fact]
    public void ColourConversionsTolerateNull()
    {
        Assert.Equal(ProcessColorRule.Defaults.Count, ColorRuleSetting.ToRules(null!).Count);
        Assert.Empty(ColorRuleSetting.FromRules(null!));
    }

    [Fact]
    public void GarbageFileFallsBackToDefaults()
    {
        var loaded = LoadFrom("{ this is not json ");
        Assert.Equal(AppSettings.Defaults.SortColumn, loaded.SortColumn);
        Assert.NotNull(loaded.ColorRules);
    }
}

/// <summary>
/// Serialises the settings tests: both drive XDG_CONFIG_HOME, which is
/// process-wide, so running them concurrently makes each read the other's
/// directory.
/// </summary>
[CollectionDefinition("settings")]
public sealed class SettingsCollection;
