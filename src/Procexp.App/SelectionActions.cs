using Avalonia.Controls;
using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Runs process actions against the current selection: guard the ghost rows,
/// dispatch through the <see cref="ActionCoordinator"/>, then refresh so the
/// result is visible immediately rather than at the next sweep.
/// </summary>
public sealed class SelectionActions(
    Window owner,
    Func<bool> confirmActions,
    ProcessListModel list,
    Func<ProcessRecord?> selection,
    Func<Task> refresh
)
{
    private readonly ActionCoordinator _coordinator = new(owner, confirmActions);

    /// <summary>
    /// The selected process, or null when the selection refers to a row that is
    /// only still on screen because it is fading out.
    /// </summary>
    /// <remarks>
    /// Acting on a ghost row would signal a process that has already exited,
    /// or worse, whatever has since inherited its pid.
    /// </remarks>
    public ProcessRecord? Actionable()
    {
        var selected = selection();
        return selected is null || list.IsDead(selected.Id) ? null : selected;
    }

    public async Task KillAsync(bool tree)
    {
        if (Actionable() is { } process)
        {
            await _coordinator.KillAsync(process, tree, list.Current).ConfigureAwait(true);
            await refresh().ConfigureAwait(true);
        }
    }

    public async Task SuspendAsync()
    {
        if (Actionable() is { } process)
        {
            await _coordinator.SuspendAsync(process).ConfigureAwait(true);
            await refresh().ConfigureAwait(true);
        }
    }

    public async Task ResumeAsync()
    {
        if (Actionable() is { } process)
        {
            await _coordinator.ResumeAsync(process).ConfigureAwait(true);
            await refresh().ConfigureAwait(true);
        }
    }

    public async Task RestartAsync()
    {
        if (Actionable() is { } process)
        {
            await _coordinator.RestartAsync(process, list.Current).ConfigureAwait(true);
            await refresh().ConfigureAwait(true);
        }
    }

    public async Task SetNiceAsync(int nice)
    {
        if (Actionable() is { } process)
        {
            await _coordinator.SetNiceAsync(process, nice).ConfigureAwait(true);
            await refresh().ConfigureAwait(true);
        }
    }
}
