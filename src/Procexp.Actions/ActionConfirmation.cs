using Procexp.Model;

namespace Procexp.Actions;

/// <summary>How strongly the user should be warned before an action proceeds.</summary>
public enum ConfirmationSeverity
{
    /// <summary>Routine; a plain confirmation is enough.</summary>
    Routine,

    /// <summary>Likely to disrupt the session or a running service.</summary>
    Disruptive,

    /// <summary>Likely to bring down the machine or the desktop.</summary>
    Critical,
}

/// <summary>What to tell the user before carrying out an action.</summary>
public sealed record ActionConfirmation
{
    public required string Title { get; init; }
    public required string Message { get; init; }
    public required ConfirmationSeverity Severity { get; init; }

    /// <summary>True when the action should not be offered at all.</summary>
    public bool IsRefused { get; init; }
}

/// <summary>
/// Decides how dangerous a control action is before it happens.
/// </summary>
/// <remarks>
/// Ports the macOS confirmation logic, with the risky cases re-derived for Linux.
/// The categories that matter here are the init process, the session and display
/// managers, and anything systemd will simply restart.
/// </remarks>
public static class ActionConfirmationPolicy
{
    /// <summary>
    /// Processes that take the whole system or desktop session down with them.
    /// </summary>
    private static readonly HashSet<string> CriticalNames = new(StringComparer.Ordinal)
    {
        "systemd", "init", "systemd-journald", "systemd-udevd", "dbus-daemon",
        "dbus-broker", "gdm", "sddm", "lightdm", "Xorg", "Xwayland",
        "gnome-shell", "kwin_wayland", "kwin_x11", "plasmashell", "wayfire",
        "sway", "mutter", "systemd-logind",
    };

    public static ActionConfirmation ForKill(ProcessRecord process, bool isTree = false)
    {
        var what = isTree ? "Kill Process Tree" : "Kill Process";
        var subject = $"{process.Name} (pid {process.Id.Pid})";

        if (process.Id.Pid == 1)
        {
            return new ActionConfirmation
            {
                Title = what,
                Message = "pid 1 is the init process. Signalling it would halt the system.",
                Severity = ConfirmationSeverity.Critical,
                IsRefused = true,
            };
        }

        if (process.Id.Pid == Environment.ProcessId)
        {
            return new ActionConfirmation
            {
                Title = what,
                Message = "That is Process Explorer itself.",
                Severity = ConfirmationSeverity.Disruptive,
                IsRefused = true,
            };
        }

        if (CriticalNames.Contains(process.Name))
        {
            return new ActionConfirmation
            {
                Title = what,
                Message = $"{subject} is a core system or session process. " +
                          "Killing it will most likely end your desktop session or destabilise the machine.",
                Severity = ConfirmationSeverity.Critical,
            };
        }

        if (process.Flags.HasFlag(ProcessFlags.Service))
        {
            var unit = process.SystemdUnit ?? "a systemd service";
            return new ActionConfirmation
            {
                Title = what,
                Message = $"{subject} belongs to {unit}. " +
                          "systemd may restart it immediately, and stopping it properly needs systemctl.",
                Severity = ConfirmationSeverity.Disruptive,
            };
        }

        if (process.Uid != CurrentUid && CurrentUid != 0)
        {
            return new ActionConfirmation
            {
                Title = what,
                Message = $"{subject} belongs to {process.UserName ?? process.Uid.ToString()}. " +
                          "You will need elevated rights to signal it.",
                Severity = ConfirmationSeverity.Disruptive,
            };
        }

        return new ActionConfirmation
        {
            Title = what,
            Message = isTree
                ? $"Kill {subject} and every process descended from it?"
                : $"Kill {subject}?",
            Severity = ConfirmationSeverity.Routine,
        };
    }

    public static ActionConfirmation ForSuspend(ProcessRecord process)
    {
        if (process.Id.Pid == 1 || CriticalNames.Contains(process.Name))
        {
            return new ActionConfirmation
            {
                Title = "Suspend Process",
                Message = $"{process.Name} is a core system or session process. " +
                          "Suspending it will freeze your desktop, and you may not be able to resume it.",
                Severity = ConfirmationSeverity.Critical,
                IsRefused = process.Id.Pid == 1,
            };
        }

        return new ActionConfirmation
        {
            Title = "Suspend Process",
            Message = $"Suspend {process.Name} (pid {process.Id.Pid})? It will stop running until resumed.",
            Severity = ConfirmationSeverity.Routine,
        };
    }

    public static ActionConfirmation ForRestart(ProcessRecord process)
    {
        if (process.Flags.HasFlag(ProcessFlags.Service))
        {
            var unit = process.SystemdUnit ?? "a systemd unit";
            return new ActionConfirmation
            {
                Title = "Restart Process",
                Message = $"{process.Name} is managed by {unit}. Restarting it here relaunches the " +
                          "command line directly, without the environment, capabilities or cgroup systemd " +
                          $"would give it. Prefer: systemctl restart {unit}",
                Severity = ConfirmationSeverity.Disruptive,
            };
        }

        return new ActionConfirmation
        {
            Title = "Restart Process",
            Message = $"Terminate {process.Name} (pid {process.Id.Pid}) and start its command line again?",
            Severity = ConfirmationSeverity.Routine,
        };
    }

    private static uint CurrentUid { get; } = GetCurrentUid();

    private static uint GetCurrentUid()
    {
        // Reading our own status avoids another P/Invoke declaration here.
        try
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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through.
        }

        return 0;
    }
}
