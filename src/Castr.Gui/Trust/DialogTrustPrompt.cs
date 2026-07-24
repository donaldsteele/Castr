using Avalonia.Controls;
using Avalonia.Threading;
using Castr.Core.Trust;
using Castr.Gui.ViewModels;
using Castr.Gui.Views;

namespace Castr.Gui.Trust;

/// <summary>
/// The GUI's <see cref="ITrustPrompt"/>: pops a modal Trust-On-First-Use dialog on the UI thread and resolves
/// to the human's accept/reject decision.
/// <para>
/// Critical contract (per the M2 QA review): <see cref="Castr.Core.Protocol.ReceiverSession"/> propagates any
/// exception this method throws straight out of its receive loop, crashing the whole session instead of
/// merely declining trust. This implementation therefore swallows every internal error and treats
/// cancellation as a graceful denial — it can only ever return <c>true</c> or <c>false</c>, never throw.
/// </para>
/// </summary>
public sealed class DialogTrustPrompt(Func<Window?> ownerProvider) : ITrustPrompt
{
    public async Task<bool> RequestTrustAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            return await Dispatcher.UIThread.InvokeAsync(() => ShowAsync(context, cancellationToken));
        }
        catch
        {
            // Any failure (dispatcher shutdown, window teardown, cancellation, unexpected error) is a
            // graceful denial — never let it escape into ReceiverSession.RunAsync.
            return false;
        }
    }

    private async Task<bool> ShowAsync(TrustPromptContext context, CancellationToken cancellationToken)
    {
        var vm = new TrustPromptViewModel(context);
        await using var registration = cancellationToken.Register(vm.Cancel);

        var owner = ownerProvider();
        var dialog = new TrustPromptDialog { DataContext = vm };

        if (owner is not null)
            _ = dialog.ShowDialog(owner); // completes when the window closes; the window closes itself on Decided
        else
            dialog.Show(); // no owner (headless / very-early startup): show non-modally so we can still decide

        bool accepted = await vm.Result.ConfigureAwait(true);
        if (dialog.IsVisible)
            dialog.Close();
        return accepted;
    }
}
