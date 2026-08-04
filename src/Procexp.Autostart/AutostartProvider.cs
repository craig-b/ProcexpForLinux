using Procexp.Model;

namespace Procexp.Autostart;

/// <summary>
/// Resolves what causes a process to be started without the user asking — the
/// Autostart Location column.
/// </summary>
public sealed class AutostartProvider : IAutostartProvider
{
    private readonly AutostartIndex _index = new();

    public AutostartIndex Index => _index;

    /// <summary>Discard the cached index, so a changed unit file is picked up.</summary>
    public void Refresh() => _index.Invalidate();

    public ValueTask<string?> AutostartLocationAsync(
        ProcessRecord process, CancellationToken cancellationToken = default)
    {
        // Kernel threads are started by the kernel, not by any definition on disk.
        if (process.Flags.HasFlag(ProcessFlags.KernelThread))
        {
            return ValueTask.FromResult<string?>(null);
        }

        // The cgroup already names the owning unit for anything systemd launched,
        // and it is authoritative: it reflects what actually started this process,
        // where matching on the executable path only infers it. Resolving that
        // name to a unit file gives the user something they can go and edit.
        if (process.SystemdUnit is { Length: > 0 } unit)
        {
            var byUnit = _index.ForUnit(unit);
            if (byUnit is not null)
            {
                return ValueTask.FromResult<string?>(byUnit.Display);
            }

            // A transient or generated unit has no file on disk, but naming it is
            // still more useful than saying nothing.
            if (unit.EndsWith(".service", StringComparison.Ordinal))
            {
                return ValueTask.FromResult<string?>($"systemd: {unit}");
            }
        }

        // Otherwise fall back to matching the executable, which is what catches
        // XDG autostart entries, cron jobs and init scripts.
        if (process.ExecutablePath is { Length: > 0 } path)
        {
            var byProgram = _index.ForProgram(path);
            if (byProgram is not null)
            {
                return ValueTask.FromResult<string?>(byProgram.Display);
            }
        }

        return ValueTask.FromResult<string?>(null);
    }
}
