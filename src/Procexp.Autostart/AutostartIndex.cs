namespace Procexp.Autostart;

/// <summary>Where an autostart definition came from.</summary>
public enum AutostartKind
{
    SystemdSystemUnit,
    SystemdUserUnit,
    XdgAutostart,
    Cron,
    SysVInit,
}

/// <summary>One definition that can launch a program without the user asking.</summary>
public sealed record AutostartEntry
{
    public required string DefinitionPath { get; init; }
    public required AutostartKind Kind { get; init; }

    /// <summary>Unit or desktop-entry name, e.g. <c>sshd.service</c>.</summary>
    public string? Name { get; init; }

    /// <summary>The executable the definition launches, when it could be determined.</summary>
    public string? ProgramPath { get; init; }

    /// <summary>Human-readable location for the Autostart Location column.</summary>
    public string Display => Kind switch
    {
        AutostartKind.SystemdSystemUnit => $"systemd: {Name ?? DefinitionPath}",
        AutostartKind.SystemdUserUnit => $"systemd --user: {Name ?? DefinitionPath}",
        AutostartKind.XdgAutostart => $"XDG autostart: {Name ?? DefinitionPath}",
        AutostartKind.Cron => $"cron: {DefinitionPath}",
        AutostartKind.SysVInit => $"init.d: {Name ?? DefinitionPath}",
        _ => DefinitionPath,
    };
}

/// <summary>
/// A lazily-built index of everything on the system that can start a program
/// automatically, keyed by the executable it launches.
/// </summary>
/// <remarks>
/// The Linux counterpart of the macOS launchd index. Where that scans two
/// directories of plists, this covers four unrelated mechanisms — systemd units
/// (system and per-user), XDG autostart entries, cron, and legacy init scripts —
/// which is the honest cost of Linux having no single answer to "what starts
/// things".
///
/// Built once on first use and cached, because it walks several hundred files.
/// </remarks>
public sealed class AutostartIndex
{
    private static readonly string[] SystemUnitDirectories =
    [
        "/etc/systemd/system",
        "/run/systemd/system",
        "/usr/lib/systemd/system",
        "/lib/systemd/system",
    ];

    private static readonly string[] SystemXdgDirectories =
    [
        "/etc/xdg/autostart",
    ];

    private readonly Lock _gate = new();
    private Dictionary<string, AutostartEntry>? _byProgram;
    private Dictionary<string, AutostartEntry>? _byUnitName;

    /// <summary>Discard the index so the next lookup rebuilds it.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _byProgram = null;
            _byUnitName = null;
        }
    }

    /// <summary>Find the definition that launches an executable, if any.</summary>
    public AutostartEntry? ForProgram(string executablePath)
    {
        EnsureBuilt();

        lock (_gate)
        {
            return _byProgram!.GetValueOrDefault(executablePath);
        }
    }

    /// <summary>Find a definition by unit name, e.g. <c>sshd.service</c>.</summary>
    public AutostartEntry? ForUnit(string unitName)
    {
        EnsureBuilt();

        lock (_gate)
        {
            return _byUnitName!.GetValueOrDefault(unitName);
        }
    }

    public int Count
    {
        get
        {
            EnsureBuilt();
            lock (_gate)
            {
                return _byProgram!.Count;
            }
        }
    }

    private void EnsureBuilt()
    {
        lock (_gate)
        {
            if (_byProgram is not null)
            {
                return;
            }

            var byProgram = new Dictionary<string, AutostartEntry>(StringComparer.Ordinal);
            var byUnit = new Dictionary<string, AutostartEntry>(StringComparer.Ordinal);

            foreach (var directory in SystemUnitDirectories)
            {
                ScanUnits(directory, AutostartKind.SystemdSystemUnit, byProgram, byUnit);
            }

            foreach (var directory in UserUnitDirectories())
            {
                ScanUnits(directory, AutostartKind.SystemdUserUnit, byProgram, byUnit);
            }

            foreach (var directory in SystemXdgDirectories.Concat(UserXdgDirectories()))
            {
                ScanDesktopEntries(directory, byProgram, byUnit);
            }

            ScanCron(byProgram);
            ScanInitD(byProgram);

            _byProgram = byProgram;
            _byUnitName = byUnit;
        }
    }

    private static IEnumerable<string> UserUnitDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;

        yield return Path.Combine(config, "systemd", "user");
        yield return "/usr/lib/systemd/user";
        yield return "/etc/systemd/user";
    }

    private static IEnumerable<string> UserXdgDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var config = string.IsNullOrEmpty(configHome) ? Path.Combine(home, ".config") : configHome;

        yield return Path.Combine(config, "autostart");
    }

    /// <summary>
    /// Index systemd units by the executable their ExecStart names.
    /// </summary>
    /// <remarks>
    /// ExecStart values may carry prefix characters — <c>-</c> to ignore failure,
    /// <c>@</c> to override argv[0], <c>+</c>, <c>!</c> and <c>!!</c> for privilege
    /// handling — which have to be stripped before the remainder is a path.
    /// </remarks>
    private static void ScanUnits(
        string directory,
        AutostartKind kind,
        Dictionary<string, AutostartEntry> byProgram,
        Dictionary<string, AutostartEntry> byUnit)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.service", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string? program = null;
            foreach (var raw in lines)
            {
                var line = raw.TrimStart();
                if (!line.StartsWith("ExecStart=", StringComparison.Ordinal))
                {
                    continue;
                }

                program = ExtractProgram(line["ExecStart=".Length..]);
                if (program is not null)
                {
                    break;
                }
            }

            var name = Path.GetFileName(file);
            var entry = new AutostartEntry
            {
                DefinitionPath = file,
                Kind = kind,
                Name = name,
                ProgramPath = program,
            };

            // Earlier directories take precedence, matching systemd's own
            // override order: /etc beats /run beats /usr/lib.
            byUnit.TryAdd(name, entry);
            if (program is not null)
            {
                byProgram.TryAdd(program, entry);
            }
        }
    }

    private static string? ExtractProgram(string value)
    {
        var text = value.Trim();

        // Strip systemd's ExecStart prefix characters.
        while (text.Length > 0 && text[0] is '-' or '@' or '+' or '!' or ':')
        {
            text = text[1..].TrimStart();
        }

        if (text.Length == 0)
        {
            return null;
        }

        // The program is the first token, which may be quoted.
        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var close = text.IndexOf(quote, 1);
            return close > 1 ? text[1..close] : null;
        }

        var space = text.IndexOf(' ');
        var program = space < 0 ? text : text[..space];

        return program.StartsWith('/') ? program : null;
    }

    /// <summary>Index XDG autostart entries by the executable their Exec names.</summary>
    private static void ScanDesktopEntries(
        string directory,
        Dictionary<string, AutostartEntry> byProgram,
        Dictionary<string, AutostartEntry> byUnit)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*.desktop", SearchOption.TopDirectoryOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string? exec = null;
            var hidden = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();

                if (line.StartsWith("Exec=", StringComparison.Ordinal) && exec is null)
                {
                    exec = line["Exec=".Length..].Trim();
                }
                else if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
                         line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
                {
                    hidden = true;
                }
            }

            // A user-level entry with Hidden=true exists specifically to cancel a
            // system-level one, so listing it would be backwards.
            if (hidden || exec is null)
            {
                continue;
            }

            var program = ResolveDesktopExec(exec);
            var name = Path.GetFileName(file);
            var entry = new AutostartEntry
            {
                DefinitionPath = file,
                Kind = AutostartKind.XdgAutostart,
                Name = name,
                ProgramPath = program,
            };

            byUnit.TryAdd(name, entry);
            if (program is not null)
            {
                byProgram.TryAdd(program, entry);
            }
        }
    }

    /// <summary>
    /// Resolve a desktop-entry Exec line to an absolute program path.
    /// </summary>
    /// <remarks>
    /// Unlike systemd, desktop entries routinely name a bare command that is
    /// resolved through PATH, so a lookup is needed to match what the sampler
    /// reports as the executable path.
    /// </remarks>
    private static string? ResolveDesktopExec(string exec)
    {
        var text = exec.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        string command;
        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var close = text.IndexOf(quote, 1);
            if (close <= 1)
            {
                return null;
            }

            command = text[1..close];
        }
        else
        {
            var space = text.IndexOf(' ');
            command = space < 0 ? text : text[..space];
        }

        if (command.StartsWith('/'))
        {
            return command;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin:/usr/local/bin";
        foreach (var directory in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, command);
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable PATH element.
            }
        }

        return null;
    }

    private static void ScanCron(Dictionary<string, AutostartEntry> byProgram)
    {
        foreach (var file in CronFiles())
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.Contains('='))
                {
                    continue;
                }

                // The command is whatever follows the schedule. Absolute paths are
                // the only ones we can match against a running process.
                var slash = line.IndexOf('/');
                if (slash < 0)
                {
                    continue;
                }

                var command = line[slash..];
                var space = command.IndexOf(' ');
                var program = space < 0 ? command : command[..space];

                byProgram.TryAdd(program, new AutostartEntry
                {
                    DefinitionPath = file,
                    Kind = AutostartKind.Cron,
                    ProgramPath = program,
                });
            }
        }
    }

    private static IEnumerable<string> CronFiles()
    {
        if (File.Exists("/etc/crontab"))
        {
            yield return "/etc/crontab";
        }

        foreach (var directory in new[] { "/etc/cron.d", "/var/spool/cron", "/var/spool/cron/crontabs" })
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private static void ScanInitD(Dictionary<string, AutostartEntry> byProgram)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles("/etc/init.d");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            byProgram.TryAdd(file, new AutostartEntry
            {
                DefinitionPath = file,
                Kind = AutostartKind.SysVInit,
                Name = Path.GetFileName(file),
                ProgramPath = file,
            });
        }
    }
}
