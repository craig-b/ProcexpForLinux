using Avalonia.Controls;
using Procexp.Actions;
using Procexp.App.Dialogs;
using Procexp.Model;

namespace Procexp.App;

/// <summary>
/// Runs control actions on behalf of the UI: confirm, act, report.
/// </summary>
/// <remarks>
/// Every path goes through <see cref="ActionConfirmationPolicy"/> first, so the
/// severity rules are enforced in one place rather than being re-derived at each
/// call site. Refused actions never reach <see cref="ProcessActions"/> at all.
/// </remarks>
public sealed class ActionCoordinator(Window owner)
{
    private readonly ProcessActions _actions = new();

    public async Task KillAsync(ProcessRecord process, bool tree, ProcessSnapshot snapshot)
    {
        var confirmation = ActionConfirmationPolicy.ForKill(process, tree);
        if (!await Confirm(confirmation).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(
            () =>
            {
                if (tree)
                {
                    _actions.KillTree(process.Id, snapshot);
                }
                else
                {
                    _actions.Kill(process.Id);
                }
            },
            tree ? "Kill Process Tree" : "Kill Process",
            process
        );
    }

    public async Task SuspendAsync(ProcessRecord process)
    {
        var confirmation = ActionConfirmationPolicy.ForSuspend(process);
        if (!await Confirm(confirmation).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(() => _actions.Suspend(process.Id), "Suspend Process", process);
    }

    /// <summary>
    /// Resume needs no confirmation: it can only undo a suspend, so there is
    /// nothing to warn about.
    /// </summary>
    public Task ResumeAsync(ProcessRecord process) =>
        RunAsync(() => _actions.Resume(process.Id), "Resume Process", process);

    public Task SetNiceAsync(ProcessRecord process, int nice) =>
        RunAsync(() => _actions.SetNice(process.Id, nice), "Set Priority", process);

    public async Task RestartAsync(ProcessRecord process, ProcessSnapshot snapshot)
    {
        var confirmation = ActionConfirmationPolicy.ForRestart(process);
        if (!await Confirm(confirmation).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(() => _actions.Restart(process, snapshot), "Restart Process", process);
    }

    private async Task<bool> Confirm(ActionConfirmation confirmation)
    {
        var proceed = await ConfirmationDialog.ShowAsync(owner, confirmation).ConfigureAwait(true);
        return proceed && !confirmation.IsRefused;
    }

    /// <summary>
    /// Perform an action, turning any provider failure into a plain explanation.
    /// </summary>
    /// <remarks>
    /// The common failures are all expected rather than exceptional — the process
    /// exited while the dialog was open, or it belongs to another user — so they
    /// are reported in those terms rather than as errors.
    /// </remarks>
    private async Task RunAsync(Action action, string title, ProcessRecord process)
    {
        try
        {
            action();
        }
        catch (ProviderException e)
        {
            var explanation = e.Kind switch
            {
                ProviderErrorKind.ProcessGone =>
                    $"{process.Name} (pid {process.Id.Pid}) has already exited.",
                ProviderErrorKind.NotPermitted =>
                    $"Not permitted to act on {process.Name} (pid {process.Id.Pid}). "
                        + $"It belongs to {process.UserName ?? process.Uid.ToString()}; "
                        + "installing the privileged helper would allow this.",
                // The client's message says why — not in the group, granted
                // after login, helper needs a restart — and each of those has a
                // different remedy, so it must reach the user verbatim.
                ProviderErrorKind.HelperUnavailable =>
                    $"The privileged helper could not be used: {e.Message}.\n\n"
                        + "See docs/HELPER.md.",
                _ => e.Message,
            };

            await ConfirmationDialog
                .ShowMessageAsync(owner, title, explanation)
                .ConfigureAwait(true);
        }
    }
}
