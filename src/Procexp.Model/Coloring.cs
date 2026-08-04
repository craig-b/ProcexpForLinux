namespace Procexp.Model;

/// <summary>Framework-neutral colour, components 0 to 1.</summary>
public readonly record struct Rgba(double R, double G, double B, double A = 1.0)
{
    /// <summary>Construct from 8-bit components.</summary>
    public static Rgba FromBytes(int r, int g, int b, double a = 1.0) =>
        new(r / 255.0, g / 255.0, b / 255.0, a);
}

/// <summary>
/// A rule mapping a process flag to a row background colour, with light and dark
/// variants. Kept framework-neutral so the model stays free of UI dependencies.
/// </summary>
public sealed record ProcessColorRule
{
    public required ProcessFlags Flag { get; init; }
    public required bool IsEnabled { get; init; }
    public required Rgba BackgroundLight { get; init; }
    public required Rgba BackgroundDark { get; init; }

    /// <summary>
    /// The default legend, mirroring Process Explorer's colours. Order matters:
    /// earlier rules win when several flags are set, so new and dead take
    /// priority over the steady-state categories.
    /// </summary>
    public static readonly IReadOnlyList<ProcessColorRule> Defaults =
    [
        new()
        {
            Flag = ProcessFlags.NewProcess,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(198, 246, 198),
            BackgroundDark = Rgba.FromBytes(40, 90, 40),
        },
        new()
        {
            Flag = ProcessFlags.DeadProcess,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(246, 198, 198),
            BackgroundDark = Rgba.FromBytes(110, 40, 40),
        },
        new()
        {
            Flag = ProcessFlags.Zombie,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(246, 220, 190),
            BackgroundDark = Rgba.FromBytes(105, 70, 35),
        },
        new()
        {
            Flag = ProcessFlags.Suspended,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(200, 200, 200),
            BackgroundDark = Rgba.FromBytes(70, 70, 70),
        },
        new()
        {
            Flag = ProcessFlags.Service,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(255, 208, 208),
            BackgroundDark = Rgba.FromBytes(90, 55, 55),
        },
        new()
        {
            Flag = ProcessFlags.OwnProcess,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(208, 208, 255),
            BackgroundDark = Rgba.FromBytes(55, 55, 90),
        },
        new()
        {
            Flag = ProcessFlags.Sandboxed,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(208, 246, 246),
            BackgroundDark = Rgba.FromBytes(40, 80, 80),
        },
        new()
        {
            Flag = ProcessFlags.Packed,
            IsEnabled = true,
            BackgroundLight = Rgba.FromBytes(230, 208, 246),
            BackgroundDark = Rgba.FromBytes(70, 50, 90),
        },
    ];

    /// <summary>
    /// Resolve the background colour for a process. Returns null when no enabled
    /// rule matches, meaning the row keeps the default background.
    /// </summary>
    public static Rgba? Background(
        ProcessFlags flags,
        IReadOnlyList<ProcessColorRule> rules,
        bool darkMode
    )
    {
        foreach (var rule in rules)
        {
            if (rule.IsEnabled && flags.HasFlag(rule.Flag))
            {
                return darkMode ? rule.BackgroundDark : rule.BackgroundLight;
            }
        }

        return null;
    }
}
