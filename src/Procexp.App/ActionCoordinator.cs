using Avalonia.Controls;
using Procexp.Actions;
using Procexp.App.Dialogs;
using Procexp.Model;
using Procexp.Privileged;

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
    // The helper seam: EPERM retries through the daemon when its socket exists.
    // Returning false when it does not keeps the original refusal — and its
    // "installing the privileged helper would allow this" explanation — intact.
    private readonly ProcessActions _actions = new()
    {
        PrivilegedSignal = static async (id, signal, cancellationToken) =>
        {
            if (!PrivilegedClient.IsAvailable)
            {
                return false;
            }

            await new PrivilegedClient()
                .SignalAsync(id, signal, cancellationToken)
                .ConfigureAwait(false);
            return true;
        },
        PrivilegedSetNice = static async (id, nice, cancellationToken) =>
        {
            if (!PrivilegedClient.IsAvailable)
            {
                return false;
            }

            await new PrivilegedClient()
                .SetNiceAsync(id, nice, cancellationToken)
                .ConfigureAwait(false);
            return true;
        },
    };

    public async Task KillAsync(ProcessRecord process, bool tree, ProcessSnapshot snapshot)
    {
        var confirmation = ActionConfirmationPolicy.ForKill(process, tree);
        if (!await Confirm(confirmation).ConfigureAwait(true))
        {
            return;
        }

        await RunAsync(
            () =>
                tree
                    ? _actions.KillTreeAsync(process.Id, snapshot)
                    : _actions.KillAsync(process.Id),
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

        await RunAsync(() => _actions.SuspendAsync(process.Id), "Suspend Process", process);
    }

    /// <summary>
    /// Resume needs no confirmation: it can only undo a suspend, so there is
    /// nothing to warn about.
    /// </summary>
    public Task ResumeAsync(ProcessRecord process) =>
        RunAsync(() => _actions.ResumeAsync(process.Id), "Resume Process", process);

    public Task SetNiceAsync(ProcessRecord process, int nice) =>
        RunAsync(() => _actions.SetNiceAsync(process.Id, nice), "Set Priority", process);

    public async Task RestartAsync(ProcessRecord process, ProcessSnapshot snapshot)
    {
        var confirmation = ActionConfirmationPolicy.ForRestart(process);
        if (!await Confirm(confirmation).ConfigureAwait(true))
        {
            return;
        }

        // Restart deliberately does not escalate: the replacement is launched as
        // this user, so killing another user's process only to respawn it under
        // the wrong identity would not be a restart at all.
        await RunAsync(
            () =>
            {
                _actions.Restart(process, snapshot);
                return Task.CompletedTask;
            },
            "Restart Process",
            process
        );
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
    private async Task RunAsync(Func<Task> action, string title, ProcessRecord process)
    {
        try
        {
            await action().ConfigureAwait(true);
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
