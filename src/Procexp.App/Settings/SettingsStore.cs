using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Procexp.App.Controls;
using Procexp.Model;

namespace Procexp.App.Settings;

/// <summary>One saved set of columns, so layouts can be switched.</summary>
public sealed record ColumnSet
{
    public required string Name { get; init; }
    public required IReadOnlyList<Column> Columns { get; init; }
}

/// <summary>Everything the app remembers between runs.</summary>
public sealed record AppSettings
{
    public IReadOnlyList<Column> Columns { get; init; } = Columns_.DefaultList;

    /// <summary>Column widths by name, so a rename cannot corrupt the layout.</summary>
    public IReadOnlyDictionary<string, double> ColumnWidths { get; init; } =
        new Dictionary<string, double>();

    public IReadOnlyList<ColumnSet> ColumnSets { get; init; } = [];

    public Column SortColumn { get; init; } = Column.Cpu;
    public bool SortDescending { get; init; } = true;
    public bool TreeMode { get; init; } = true;
    public bool ShowLowerPane { get; init; } = true;
    public LowerPaneMode LowerPaneMode { get; init; } = LowerPaneMode.Modules;
    public double RefreshSeconds { get; init; } = 1.0;
    public bool HighlightNewAndDead { get; init; } = true;
    public bool AlwaysOnTop { get; init; }
    public double NamePaneWidth { get; init; } = 260;

    public double WindowWidth { get; init; } = 1200;
    public double WindowHeight { get; init; } = 760;

    public static readonly AppSettings Defaults = new();
}

/// <summary>
/// Loads and saves settings under the XDG config directory.
/// </summary>
/// <remarks>
/// Replaces the macOS UserDefaults store. Plain JSON at
/// <c>$XDG_CONFIG_HOME/procexp/settings.json</c>, which is inspectable and
/// editable — the Linux convention, and useful when a bad saved layout would
/// otherwise be unfixable from inside the app.
///
/// Loading never throws. A corrupt or half-written file falls back to defaults,
/// because refusing to start over a settings file would be a poor trade.
/// </remarks>
public static class SettingsStore
{
    // Source-generated rather than reflection-based. Reflection serialisation
    // is the one thing in this codebase that blocks trimming and Native AOT:
    // the trimmer cannot see which types get serialised, and AOT has no way to
    // generate the converters at runtime.
    private static JsonTypeInfo<AppSettings> TypeInfo => SettingsJsonContext.Default.AppSettings;

    public static string ConfigDirectory
    {
        get
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrEmpty(xdg)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".config"
                )
                : xdg;

            return Path.Combine(root, "procexp");
        }
    }

    public static string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return AppSettings.Defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            var loaded = JsonSerializer.Deserialize(json, TypeInfo);

            return loaded is null ? AppSettings.Defaults : Sanitise(loaded);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return AppSettings.Defaults;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);

            // Write to a temporary file and move it into place, so a crash or a
            // full disk mid-write cannot leave a truncated settings file that
            // fails to parse on next start.
            var temporary = SettingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, TypeInfo));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing settings is not worth interrupting the user over.
        }
    }

    /// <summary>
    /// Repair anything a hand-edited or outdated file could get wrong.
    /// </summary>
    /// <remarks>
    /// Settings files outlive the code that wrote them. A column removed in a
    /// later version, a duplicate, or a missing pinned column would each break
    /// the list in a way the user could not diagnose.
    /// </remarks>
    private static AppSettings Sanitise(AppSettings settings)
    {
        var columns = Columns_.Normalise(settings.Columns);

        return settings with
        {
            Columns = columns,
            ColumnSets =
            [
                .. settings.ColumnSets.Select(s =>
                    s with
                    {
                        Columns = Columns_.Normalise(s.Columns),
                    }
                ),
            ],
            RefreshSeconds = Math.Clamp(settings.RefreshSeconds, 0.25, 60),
            NamePaneWidth = Math.Clamp(settings.NamePaneWidth, 120, 900),
            WindowWidth = Math.Clamp(settings.WindowWidth, 640, 10000),
            WindowHeight = Math.Clamp(settings.WindowHeight, 480, 10000),
            SortColumn = Model.Columns.IsSupported(settings.SortColumn)
                ? settings.SortColumn
                : Column.Cpu,
        };
    }
}

/// <summary>
/// Source-generated serialisation for the settings file.
/// </summary>
/// <remarks>
/// <c>UseStringEnumConverter</c> rather than a <c>JsonStringEnumConverter</c>
/// instance: the non-generic converter needs runtime code generation, which is
/// precisely what Native AOT cannot do. Writing enums as names keeps the file
/// readable and survives the numeric values being reordered.
/// </remarks>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Column list helpers.
/// </summary>
/// <remarks>
/// Named with a trailing underscore because <c>Columns</c> is already the model's
/// static formatting class, and having both in scope in the settings code would
/// force one to be qualified everywhere.
/// </remarks>
public static class Columns_
{
    public static IReadOnlyList<Column> DefaultList => Model.Columns.Default;

    /// <summary>
    /// Make a column list usable: drop unsupported and duplicate entries, and
    /// ensure the pinned ones are present and first.
    /// </summary>
    public static IReadOnlyList<Column> Normalise(IReadOnlyList<Column> columns)
    {
        var seen = new HashSet<Column>();
        var result = new List<Column>(columns.Count);

        foreach (var column in Model.Columns.Pinned)
        {
            if (seen.Add(column))
            {
                result.Add(column);
            }
        }

        foreach (var column in columns)
        {
            if (Model.Columns.IsSupported(column) && seen.Add(column))
            {
                result.Add(column);
            }
        }

        return result;
    }
}
